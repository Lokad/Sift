using System.Diagnostics;
using Lokad.Sift.Indexer;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Search;
using Lokad.Sift.Storage;

namespace Lokad.Sift;

public sealed class Sift : ISift
{
    private readonly ISiftStorage _storage;
    private readonly IndexBuilder _builder;
    private readonly SiftOptions _options;
    private readonly CancellationTokenSource _reclamationCancellation = new();
    private readonly ManualResetEventSlim _reclamationIdle = new(initialState: true);
    // Makes worker ownership and the idle signal atomic with respect to new requests and Dispose.
    private readonly object _reclamationSync = new();
    private int _activeWriters;
    private int _currentActivity;
    private int _reclamationRequests;
    private int _reclamationWorkerActive;
    private int _disposed;
    internal ISiftStorage Storage => _storage;

    public Sift(string rootPath, SiftOptions? options = null)
        : this(new CorpusStorage(rootPath), options)
    {
    }

    internal Sift(ISiftStorage storage, SiftOptions? options = null)
    {
        _options = options ?? new SiftOptions();
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _builder = new IndexBuilder(_storage, _options);
        _storage.EnsureSnapshotMetadata();
        ScheduleReclamationIfPending();
    }

    public static Sift CreateInMemory(SiftOptions? options = null)
    {
        return new Sift(new InMemorySiftStorage(), options);
    }

    public IIndexSnapshot OpenSnapshot(SearchOptions options = default)
    {
        ThrowIfDisposed();
        return new SearchSnapshot(_storage, _options, options, ScheduleReclamationIfPending);
    }

    public IOverlaySnapshot OpenOverlaySnapshot(SearchOptions options = default)
    {
        ThrowIfDisposed();
        return new OverlaySnapshot(
            new SearchSnapshot(_storage, _options, options, ScheduleReclamationIfPending),
            _options,
            options);
    }

    public Task<BuildStats> Build(IDocumentSource documents, CancellationToken cancellationToken = default)
    {
        var activity = BeginActivity(SiftActivity.Build);
        return Task.Run(async () =>
        {
            try
            {
                using var measurement = ManagedMemoryPeakMeasurement.Start();
                var stats = BuildCore(documents, cancellationToken);
                var peakManagedBytes = await measurement.CompleteAsync().ConfigureAwait(false);
                return stats with { PeakManagedBytes = peakManagedBytes };
            }
            finally
            {
                activity.Dispose();
            }
        });
    }

    private BuildStats BuildCore(IDocumentSource documents, CancellationToken cancellationToken)
    {
        return _builder.Build(documents, cancellationToken);
    }

    public ActivityStats GetActivityStats()
    {
        ThrowIfDisposed();
        var isBusy = Volatile.Read(ref _activeWriters) > 0;
        var currentActivity = (SiftActivity)Volatile.Read(ref _currentActivity);
        return new ActivityStats(isBusy, isBusy ? currentActivity : SiftActivity.Idle);
    }

    public MaintenanceStats GetMaintenanceStats()
    {
        ThrowIfDisposed();
        using var snapshot = _storage.OpenReadSnapshot();
        var manifest = snapshot.Manifest;
        var segmentCount = manifest.Segments.Count;
        var totalDocuments = 0;
        var deadDocuments = 0;
        long contentBytes = 0;
        long reclaimableContentBytes = 0;
        var segmentsWithDeletions = 0;
        var smallSegmentCount = 0;
        var smallDocumentThreshold = Math.Max(1, _options.TargetSegmentDocumentCount / 4);
        var smallContentThreshold = Math.Max(1L, _options.TargetSegmentContentBytes / 4);

        foreach (var segment in manifest.Segments)
        {
            totalDocuments += segment.DocumentCount;
            deadDocuments += segment.DeadOrdinals.Length;
            contentBytes += segment.ContentBytes;
            if (segment.DeadOrdinals.Length > 0)
            {
                segmentsWithDeletions++;
                using var mappedSegment = _storage.OpenSegment(
                    segment.SegmentId,
                    validateChecksums: false);
                foreach (var deadOrdinal in segment.DeadOrdinals)
                {
                    reclaimableContentBytes = checked(
                        reclaimableContentBytes +
                        mappedSegment.GetDocument(deadOrdinal).ContentLength);
                }
            }

            if (segment.LiveDocumentCount > 0 &&
                (segment.LiveDocumentCount <= smallDocumentThreshold ||
                 segment.ContentBytes <= smallContentThreshold))
            {
                smallSegmentCount++;
            }
        }

        var liveDocuments = totalDocuments - deadDocuments;
        var deadDocumentFraction = totalDocuments == 0 ? 0d : (double)deadDocuments / totalDocuments;
        var estimatedLiveContentBytes = contentBytes - reclaimableContentBytes;

        return new MaintenanceStats(
            Generation: manifest.Generation,
            SegmentCount: segmentCount,
            TotalDocuments: totalDocuments,
            LiveDocuments: liveDocuments,
            DeadDocuments: deadDocuments,
            DeadDocumentFraction: deadDocumentFraction,
            ContentBytes: contentBytes,
            EstimatedLiveContentBytes: estimatedLiveContentBytes,
            EstimatedReclaimableContentBytes: reclaimableContentBytes,
            DeadOrdinalEntries: deadDocuments,
            SegmentsWithDeletions: segmentsWithDeletions,
            SmallSegmentCount: smallSegmentCount);
    }

    public StorageStats GetStorageStats()
    {
        ThrowIfDisposed();
        return _storage.GetStorageStats();
    }

    public Task<UpdateStats> Update(IDocumentSource upserts, ReadOnlySpan<DocumentKey> deletions, CancellationToken cancellationToken = default)
    {
        var deletionArray = deletions.ToArray();
        var activity = BeginActivity(SiftActivity.Update);
        return Task.Run(() =>
        {
            try
            {
                return UpdateCore(upserts, deletionArray, cancellationToken);
            }
            finally
            {
                activity.Dispose();
            }
        });
    }

    private UpdateStats UpdateCore(IDocumentSource upserts, ReadOnlySpan<DocumentKey> deletions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upserts);

        var watch = Stopwatch.StartNew();
        using var _ = _storage.AcquireWriterLock(cancellationToken);
        var existingManifest = _storage.HasManifest
            ? _storage.ReadManifest()
            : new CorpusManifest(Guid.NewGuid(), 0, []);

        var upsertLoad = IndexBuilder.LoadDocuments(upserts, cancellationToken, _options);
        var upsertsByPath = new Dictionary<string, StoredDocument>(StringComparer.Ordinal);
        foreach (var document in upsertLoad.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!upsertsByPath.TryAdd(document.Path, document))
            {
                throw new InvalidOperationException($"Duplicate canonical path in update batch: '{document.Path}'.");
            }
        }

        var submittedUpsertPaths = new HashSet<string>(upsertsByPath.Keys, StringComparer.Ordinal);
        submittedUpsertPaths.UnionWith(upsertLoad.RejectedPaths);
        var deletionSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deletion in deletions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = IndexBuilder.CanonicalizePath(deletion.Path);
            if (!deletionSet.Add(normalized))
            {
                throw new InvalidOperationException($"Duplicate deletion path in update batch: '{normalized}'.");
            }

            if (submittedUpsertPaths.Contains(normalized))
            {
                throw new InvalidOperationException($"Path '{normalized}' cannot appear in both upserts and deletions.");
            }
        }

        var affectedPaths = new HashSet<string>(submittedUpsertPaths, StringComparer.Ordinal);
        affectedPaths.UnionWith(deletionSet);
        var liveDocumentKeys = FindLiveDocumentKeys(
            existingManifest,
            affectedPaths,
            cancellationToken);
        var newSegments = new List<SegmentManifestEntry>(existingManifest.Segments.Count + 1);
        var deadBySegment = existingManifest.Segments.ToDictionary(
            static segment => segment.SegmentId,
            static segment => new HashSet<uint>(segment.DeadOrdinals),
            EqualityComparer<ulong>.Default);

        foreach (var document in upsertsByPath.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (liveDocumentKeys.TryGetValue(document.Path, out var current))
            {
                deadBySegment[current.SegmentId].Add(current.Ordinal);
            }
        }

        // A rejected replacement must hide the previous version; otherwise callers that
        // checkpoint the rejection would keep stale content searchable indefinitely.
        foreach (var path in upsertLoad.RejectedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (liveDocumentKeys.TryGetValue(path, out var current))
            {
                deadBySegment[current.SegmentId].Add(current.Ordinal);
            }
        }

        foreach (var path in deletionSet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (liveDocumentKeys.TryGetValue(path, out var current))
            {
                deadBySegment[current.SegmentId].Add(current.Ordinal);
            }
        }

        foreach (var segment in existingManifest.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            newSegments.Add(segment with { DeadOrdinals = deadBySegment[segment.SegmentId].OrderBy(static value => value).ToArray() });
        }

        if (upsertLoad.Documents.Count > 0)
        {
            upsertLoad.Documents.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
            var partitions = IndexBuilder.PartitionDocuments(upsertLoad.Documents, _options);
            var segmentSeed = (existingManifest.Generation + 1UL) ^ ((ulong)DateTime.UtcNow.Ticks << 1);
            for (var i = 0; i < partitions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                newSegments.Add(_storage.PublishSegment(partitions[i], segmentSeed + (ulong)i, cancellationToken));
            }
        }

        var manifest = new CorpusManifest(existingManifest.CorpusId, existingManifest.Generation + 1UL, newSegments);
        _storage.PublishManifest(manifest);
        watch.Stop();
        return new UpdateStats(
            SubmittedUpserts: upsertLoad.Submitted,
            UpsertedDocuments: upsertLoad.Documents.Count,
            RejectedUpserts: upsertLoad.Rejected,
            DeletedDocuments: deletionSet.Count,
            PublishedSegments: upsertLoad.Documents.Count > 0 ? newSegments.Count - existingManifest.Segments.Count : 0,
            SubmittedBytes: upsertLoad.SubmittedBytes,
            ContentBytes: upsertLoad.AcceptedBytes,
            RejectedBytes: upsertLoad.RejectedBytes,
            ElapsedMilliseconds: watch.ElapsedMilliseconds);
    }

    public Task<CompactStats> Compact(CompactOptions options, CancellationToken cancellationToken = default)
    {
        ValidateCompactOptions(options);
        var activity = BeginActivity(SiftActivity.Compact);
        return Task.Run(async () =>
        {
            try
            {
                using var measurement = ManagedMemoryPeakMeasurement.Start();
                var stats = CompactCore(options, cancellationToken);
                var peakManagedBytes = await measurement.CompleteAsync().ConfigureAwait(false);
                return stats with { PeakManagedBytes = peakManagedBytes };
            }
            finally
            {
                activity.Dispose();
            }
        });
    }

    private CompactStats CompactCore(CompactOptions options, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var _ = _storage.AcquireWriterLock(cancellationToken);
        var manifest = _storage.HasManifest
            ? _storage.ReadManifest()
            : new CorpusManifest(Guid.NewGuid(), 0, []);
        if (manifest.Segments.Count <= 1 && manifest.Segments.All(static segment => segment.DeadOrdinals.Length == 0))
        {
            watch.Stop();
            return new CompactStats(
                InputSegments: manifest.Segments.Count,
                OutputSegments: manifest.Segments.Count,
                MergedDocuments: manifest.Segments.Sum(static segment => segment.LiveDocumentCount),
                ContentBytes: manifest.Segments.Sum(static segment => segment.ContentBytes),
                ElapsedMilliseconds: watch.ElapsedMilliseconds,
                PeakManagedBytes: 0);
        }

        var selectedSegmentIds = SelectSegmentsForCompaction(manifest, options);
        if (selectedSegmentIds.Count == 0)
        {
            watch.Stop();
            return new CompactStats(
                InputSegments: manifest.Segments.Count,
                OutputSegments: manifest.Segments.Count,
                MergedDocuments: 0,
                ContentBytes: 0,
                ElapsedMilliseconds: watch.ElapsedMilliseconds,
                PeakManagedBytes: 0);
        }

        _storage.EnsureReplacementAllowedUnderWriterLock();
        var segmentSeed = (manifest.Generation + 1UL) ^ ((ulong)DateTime.UtcNow.Ticks << 1);
        var publication = PublishCompactedSegments(manifest, selectedSegmentIds, segmentSeed, options, cancellationToken);
        var newSegments = publication.Segments;

        var retainedSegments = manifest.Segments
            .Where(entry => !selectedSegmentIds.Contains(entry.SegmentId))
            .ToArray();
        var mergedSegments = new SegmentManifestEntry[retainedSegments.Length + newSegments.Count];
        Array.Copy(retainedSegments, mergedSegments, retainedSegments.Length);
        for (var i = 0; i < newSegments.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mergedSegments[retainedSegments.Length + i] = newSegments[i];
        }

        var newManifest = new CorpusManifest(manifest.CorpusId, manifest.Generation + 1UL, mergedSegments);
        _storage.PublishManifest(newManifest);
        watch.Stop();
        return new CompactStats(
            InputSegments: selectedSegmentIds.Count,
            OutputSegments: newSegments.Count,
            MergedDocuments: publication.MergedDocuments,
            ContentBytes: publication.ContentBytes,
            ElapsedMilliseconds: watch.ElapsedMilliseconds,
            PeakManagedBytes: 0);
    }

    public void Dispose()
    {
        lock (_reclamationSync)
        {
            if (_disposed != 0)
            {
                return;
            }

            Volatile.Write(ref _disposed, 1);
            _reclamationCancellation.Cancel();
        }

        _reclamationIdle.Wait();
        _reclamationCancellation.Dispose();
        _reclamationIdle.Dispose();
    }

    private void ScheduleReclamationIfPending()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            if (!_storage.IsReclamationPending)
            {
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        lock (_reclamationSync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _reclamationRequests = 1;
            if (_reclamationWorkerActive != 0)
            {
                return;
            }

            _reclamationWorkerActive = 1;
            _reclamationIdle.Reset();
            ThreadPool.UnsafeQueueUserWorkItem(
                static owner => owner.RunScheduledReclamation(),
                this,
                preferLocal: false);
        }
    }

    private void RunScheduledReclamation()
    {
        try
        {
            while (true)
            {
                lock (_reclamationSync)
                {
                    _reclamationRequests = 0;
                }

                try
                {
                    _storage.ReclaimUnreferencedSegments(_reclamationCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                }

                lock (_reclamationSync)
                {
                    if (_disposed == 0 && _reclamationRequests != 0)
                    {
                        continue;
                    }

                    _reclamationWorkerActive = 0;
                    _reclamationIdle.Set();
                    return;
                }
            }
        }
        finally
        {
            lock (_reclamationSync)
            {
                if (_reclamationWorkerActive != 0)
                {
                    _reclamationWorkerActive = 0;
                    _reclamationIdle.Set();
                }
            }
        }
    }

    private static void ValidateCompactOptions(CompactOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxMergedSegments <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMergedSegments must be positive.");
        }

        if (!double.IsFinite(options.DeadFractionThreshold) ||
            options.DeadFractionThreshold < 0d ||
            options.DeadFractionThreshold > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "DeadFractionThreshold must be between zero and one.");
        }

        if (options.SegmentCountTrigger < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SegmentCountTrigger must be positive.");
        }

        if (options.MetadataBloatDeadOrdinalsThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MetadataBloatDeadOrdinalsThreshold must be positive.");
        }

        if (options.MaxInputContentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxInputContentBytes must be positive.");
        }
    }

    private IDisposable BeginActivity(SiftActivity activity)
    {
        ThrowIfDisposed();
        if (Interlocked.Increment(ref _activeWriters) == 1)
        {
            Volatile.Write(ref _currentActivity, (int)activity);
        }

        return new ActivityScope(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private sealed class ActivityScope : IDisposable
    {
        private Sift? _owner;

        public ActivityScope(Sift owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            if (Interlocked.Decrement(ref owner._activeWriters) == 0)
            {
                Volatile.Write(ref owner._currentActivity, (int)SiftActivity.Idle);
            }
        }
    }

    private sealed class ManagedMemoryPeakMeasurement : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _sampler;
        private long _peakManagedBytes;

        private ManagedMemoryPeakMeasurement()
        {
            _peakManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
            _sampler = Task.Run(async () =>
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
                    if (managedBytes > Volatile.Read(ref _peakManagedBytes))
                    {
                        Interlocked.Exchange(ref _peakManagedBytes, managedBytes);
                    }

                    try
                    {
                        await Task.Delay(5, _cancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        public static ManagedMemoryPeakMeasurement Start()
        {
            return new ManagedMemoryPeakMeasurement();
        }

        public async Task<long> CompleteAsync()
        {
            var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
            if (managedBytes > Volatile.Read(ref _peakManagedBytes))
            {
                Interlocked.Exchange(ref _peakManagedBytes, managedBytes);
            }

            _cancellation.Cancel();
            try
            {
                await _sampler.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            managedBytes = GC.GetTotalMemory(forceFullCollection: false);
            if (managedBytes > Volatile.Read(ref _peakManagedBytes))
            {
                Interlocked.Exchange(ref _peakManagedBytes, managedBytes);
            }

            return Volatile.Read(ref _peakManagedBytes);
        }

        public void Dispose()
        {
            _cancellation.Dispose();
        }
    }

    private Dictionary<string, LiveDocumentKey> FindLiveDocumentKeys(
        CorpusManifest manifest,
        IReadOnlySet<string> affectedPaths,
        CancellationToken cancellationToken)
    {
        var live = new Dictionary<string, LiveDocumentKey>(affectedPaths.Count, StringComparer.Ordinal);
        if (affectedPaths.Count == 0)
        {
            return live;
        }

        foreach (var entry in manifest.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var segment = _storage.OpenSegment(entry.SegmentId, validateChecksums: false);
            foreach (var path in affectedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!segment.TryFindDocumentOrdinal(path, out var ordinal) ||
                    Array.BinarySearch(entry.DeadOrdinals, ordinal) >= 0)
                {
                    continue;
                }

                live[path] = new LiveDocumentKey(entry.SegmentId, ordinal);
            }
        }

        return live;
    }

    private CompactionPublication PublishCompactedSegments(CorpusManifest manifest, HashSet<ulong> selectedSegmentIds, ulong segmentSeed, CompactOptions options, CancellationToken cancellationToken)
    {
        var cursors = new List<CompactionCursor>();
        try
        {
            foreach (var entry in manifest.Segments)
            {
                if (!selectedSegmentIds.Contains(entry.SegmentId))
                {
                    continue;
                }

                var cursor = new CompactionCursor(_storage.OpenSegment(entry.SegmentId, validateChecksums: false), entry.DeadOrdinals);
                if (cursor.MoveNext())
                {
                    cursors.Add(cursor);
                }
                else
                {
                    cursor.Dispose();
                }
            }

            var queue = new PriorityQueue<CompactionCursor, CompactionPriority>(CompactionPriorityComparer.Instance);
            for (var i = 0; i < cursors.Count; i++)
            {
                queue.Enqueue(cursors[i], new CompactionPriority(cursors[i].Segment, cursors[i].Current.Document));
            }

            var published = new List<SegmentManifestEntry>();
            var currentPartition = new List<CompactionDocument>();
            long currentBytes = 0;
            var mergedDocuments = 0;
            long contentBytes = 0;

            while (queue.TryDequeue(out var cursor, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = cursor.Current;
                if (currentPartition.Count > 0 &&
                    (currentPartition.Count >= _options.TargetSegmentDocumentCount || currentBytes + next.ContentLength > _options.TargetSegmentContentBytes))
                {
                    published.Add(_storage.PublishSegment(currentPartition, segmentSeed + (ulong)published.Count, cancellationToken));
                    currentPartition = new List<CompactionDocument>();
                    currentBytes = 0;
                }

                currentPartition.Add(next);
                currentBytes += next.ContentLength;
                mergedDocuments++;
                contentBytes += next.ContentLength;

                if (cursor.MoveNext())
                {
                    queue.Enqueue(cursor, new CompactionPriority(cursor.Segment, cursor.Current.Document));
                }
            }

            if (currentPartition.Count > 0)
            {
                published.Add(_storage.PublishSegment(currentPartition, segmentSeed + (ulong)published.Count, cancellationToken));
            }

            return new CompactionPublication(published, mergedDocuments, contentBytes);
        }
        finally
        {
            for (var i = 0; i < cursors.Count; i++)
            {
                cursors[i].Dispose();
            }
        }
    }

    private HashSet<ulong> SelectSegmentsForCompaction(CorpusManifest manifest, CompactOptions options)
    {
        var smallDocumentThreshold = Math.Max(1, _options.TargetSegmentDocumentCount / 4);
        var smallContentThreshold = Math.Max(1L, _options.TargetSegmentContentBytes / 4);
        var ordered = manifest.Segments
            .Select(entry => new
            {
                Entry = entry,
                DeadFraction = entry.DocumentCount == 0 ? 0d : (double)entry.DeadOrdinals.Length / entry.DocumentCount,
                IsSmallSegment = entry.LiveDocumentCount > 0 &&
                    (entry.LiveDocumentCount <= smallDocumentThreshold ||
                     entry.ContentBytes <= smallContentThreshold),
                HasMetadataBloat = entry.DeadOrdinals.Length >= options.MetadataBloatDeadOrdinalsThreshold
            })
            .Where(item =>
                item.DeadFraction >= options.DeadFractionThreshold ||
                item.HasMetadataBloat ||
                (manifest.Segments.Count > options.SegmentCountTrigger && item.IsSmallSegment))
            .OrderByDescending(item => item.DeadFraction)
            .ThenByDescending(item => item.HasMetadataBloat)
            .ThenBy(item => item.Entry.LiveDocumentCount)
            .ThenBy(item => item.Entry.SegmentId)
            .ToArray();

        var selected = new HashSet<ulong>();
        long selectedContentBytes = 0;
        foreach (var item in ordered)
        {
            if (selected.Count >= options.MaxMergedSegments)
            {
                break;
            }

            if (selected.Count > 0 &&
                item.Entry.ContentBytes > options.MaxInputContentBytes - selectedContentBytes)
            {
                continue;
            }

            selected.Add(item.Entry.SegmentId);
            selectedContentBytes += item.Entry.ContentBytes;
        }

        return selected;
    }

    private readonly record struct LiveDocumentKey(
        ulong SegmentId,
        uint Ordinal);

    private sealed class Utf8PathComparer : IComparer<byte[]>
    {
        public static Utf8PathComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            return left.AsSpan().SequenceCompareTo(right);
        }
    }

    private readonly record struct CompactionPriority(
        MappedSegment Segment,
        SegmentDocument Document);

    private sealed class CompactionPriorityComparer : IComparer<CompactionPriority>
    {
        public static CompactionPriorityComparer Instance { get; } = new();

        public int Compare(CompactionPriority left, CompactionPriority right)
        {
            return left.Segment.GetPathBytes(left.Document).SequenceCompareTo(right.Segment.GetPathBytes(right.Document));
        }
    }

    private sealed class CompactionCursor : IDisposable
    {
        private readonly uint[] _deadOrdinals;
        private int _deadIndex;
        private uint _nextOrdinal;

        public CompactionCursor(MappedSegment segment, uint[] deadOrdinals)
        {
            Segment = segment;
            _deadOrdinals = deadOrdinals;
            Current = null!;
        }

        public MappedSegment Segment { get; }
        public CompactionDocument Current { get; private set; }

        public bool MoveNext()
        {
            while (_nextOrdinal < Segment.DocumentCount)
            {
                var ordinal = _nextOrdinal++;
                while (_deadIndex < _deadOrdinals.Length && _deadOrdinals[_deadIndex] < ordinal)
                {
                    _deadIndex++;
                }

                if (_deadIndex < _deadOrdinals.Length && _deadOrdinals[_deadIndex] == ordinal)
                {
                    continue;
                }

                Current = CreateCompactionDocument(Segment, Segment.GetDocument(ordinal));
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            Segment.Dispose();
        }
    }

    private static CompactionDocument CreateCompactionDocument(MappedSegment segment, SegmentDocument document)
    {
        return new CompactionDocument
        {
            Segment = segment,
            Document = document,
            LineCount = document.LineCount,
            Flags = document.Flags,
            MTimeUtcTicks = document.MTimeUtcTicks,
            ContentHash64 = document.ContentHash64
        };
    }
}

internal readonly record struct CompactionPublication(
    List<SegmentManifestEntry> Segments,
    int MergedDocuments,
    long ContentBytes);

internal sealed class CompactionDocument : ISegmentBuildDocument
{
    public required MappedSegment Segment { get; init; }
    public required SegmentDocument Document { get; init; }
    public required int LineCount { get; init; }
    public required ushort Flags { get; init; }
    public required long MTimeUtcTicks { get; init; }
    public required ulong ContentHash64 { get; init; }
    public int ContentLength => Document.ContentLength;

    public ReadOnlySpan<byte> GetPathUtf8()
    {
        var document = Document;
        return Segment.GetPathBytes(document);
    }

    public ReadOnlySpan<byte> GetContentBytes()
    {
        var document = Document;
        return Segment.GetContentBytes(document);
    }

    public ReadOnlySpan<byte> GetEncodedLineIndex()
    {
        var document = Document;
        return Segment.GetEncodedLineIndexBytes(document);
    }
}

