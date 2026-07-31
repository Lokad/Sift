using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Lokad.Sift.Indexer;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Search;
using Lokad.Sift.Storage;

namespace Lokad.Sift;

public sealed class Sift : ISift
{
    private const long EstimatedReplacementPackOverheadBytes = 64L * 1024;
    private readonly ISiftStorage _storage;
    private readonly IndexBuilder _builder;
    private readonly SiftOptions _options;
    private readonly CancellationTokenSource _writerCancellation = new();
    private readonly ManualResetEventSlim _writersIdle = new(initialState: true);
    private readonly CancellationTokenSource _reclamationCancellation = new();
    private readonly ManualResetEventSlim _reclamationIdle = new(initialState: true);
    private readonly ConcurrentDictionary<LegacyMaintenanceKey, long> _legacyDeadContentBytes = new();
    // Makes worker ownership and the idle signal atomic with respect to new requests and Dispose.
    private readonly object _reclamationSync = new();
    // Prevents Dispose from racing a mutation between its disposed check and activity registration.
    private readonly object _activitySync = new();
    private int _activeWriters;
    private int _currentActivity;
    private int _reclamationRequests;
    private int _reclamationWorkerActive;
    private int _disposed;
    private Exception? _rebuildCause;
    internal ISiftStorage Storage => _storage;
    public bool RequiresRebuild => Volatile.Read(ref _rebuildCause) is not null;

    public Sift(string rootPath)
        : this(rootPath, new SiftOptions())
    {
    }

    public Sift(string rootPath, SiftOptions options)
        : this(new CorpusStorage(rootPath, options.MaximumPhysicalBytes), options)
    {
    }

    internal Sift(ISiftStorage storage, SiftOptions options)
    {
        _options = options;
        ValidateSiftOptions(_options);
        _storage = storage;
        _builder = new IndexBuilder(_storage, _options);
        try
        {
            _storage.EnsureSnapshotMetadata();
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            _rebuildCause = exception;
            return;
        }

        ScheduleReclamationIfPending();
    }

    public static Sift CreateInMemory()
    {
        return CreateInMemory(new SiftOptions());
    }

    public static Sift CreateInMemory(SiftOptions options)
    {
        return new Sift(new InMemorySiftStorage(), options);
    }

    public IIndexSnapshot OpenSnapshot()
    {
        return OpenSnapshot(new SearchOptions());
    }

    public IIndexSnapshot OpenSnapshot(SearchOptions options)
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        try
        {
            return new SearchSnapshot(
                _storage,
                _options,
                options,
                ScheduleReclamationIfPending,
                MarkRequiresRebuild);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    public IOverlaySnapshot OpenOverlaySnapshot()
    {
        return OpenOverlaySnapshot(new SearchOptions());
    }

    public IOverlaySnapshot OpenOverlaySnapshot(SearchOptions options)
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        try
        {
            return new OverlaySnapshot(
                new SearchSnapshot(
                    _storage,
                    _options,
                    options,
                    ScheduleReclamationIfPending,
                    MarkRequiresRebuild),
                _options,
                options);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    public Task<BuildStats> Build(IDocumentSource documents)
    {
        return Build(documents, CancellationToken.None);
    }

    public Task<BuildStats> Build(IDocumentSource documents, CancellationToken cancellationToken)
    {
        var activity = BeginActivity(SiftActivity.Build);
        return Task.Run(async () =>
        {
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _writerCancellation.Token);
            try
            {
                using var measurement = ManagedMemoryPeakMeasurement.Start();
                var stats = BuildCore(documents, operationCancellation.Token);
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
        var isRecovery = RequiresRebuild;
        try
        {
            var stats = _builder.Build(documents, isRecovery, cancellationToken);
            Volatile.Write(ref _rebuildCause, null);
            return stats;
        }
        catch (Exception exception) when (isRecovery)
        {
            MarkRequiresRebuild(new InvalidDataException(
                "The full Sift rebuild did not complete.",
                exception));
            throw;
        }
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
        ThrowIfRebuildRequired();
        try
        {
            return GetMaintenanceStatsCore();
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    private MaintenanceStats GetMaintenanceStatsCore()
    {
        using var snapshot = _storage.OpenReadSnapshot();
        var manifest = snapshot.Manifest;
        var segmentCount = manifest.Segments.Count;
        var totalDocuments = 0;
        var deadDocuments = 0;
        long contentBytes = 0;
        long reclaimableContentBytes = 0;
        var segmentsWithDeletions = 0;
        var smallSegmentCount = 0;
        var currentLegacyKeys = new HashSet<LegacyMaintenanceKey>();

        foreach (var segment in manifest.Segments)
        {
            totalDocuments += segment.DocumentCount;
            deadDocuments += segment.DeadOrdinals.Length;
            contentBytes += segment.ContentBytes;
            if (segment.DeadOrdinals.Length > 0)
            {
                segmentsWithDeletions++;
                reclaimableContentBytes = checked(
                    reclaimableContentBytes +
                    GetDeadContentBytes(segment, currentLegacyKeys, CancellationToken.None));
            }

            if (segment.LiveDocumentCount > 0 &&
                segment.LiveDocumentCount < _options.TargetSegmentDocumentCount &&
                segment.ContentBytes < _options.TargetSegmentContentBytes)
            {
                smallSegmentCount++;
            }
        }

        foreach (var key in _legacyDeadContentBytes.Keys)
        {
            if (!currentLegacyKeys.Contains(key))
            {
                _legacyDeadContentBytes.TryRemove(key, out _);
            }
        }

        var liveDocuments = totalDocuments - deadDocuments;
        var deadDocumentFraction = totalDocuments == 0 ? 0d : (double)deadDocuments / totalDocuments;
        var deadContentFraction = contentBytes == 0 ? 0d : (double)reclaimableContentBytes / contentBytes;
        var estimatedLiveContentBytes = contentBytes - reclaimableContentBytes;

        return new MaintenanceStats(
            Generation: manifest.Generation,
            SegmentCount: segmentCount,
            TotalDocuments: totalDocuments,
            LiveDocuments: liveDocuments,
            DeadDocuments: deadDocuments,
            DeadDocumentFraction: deadDocumentFraction,
            DeadContentFraction: deadContentFraction,
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
        ThrowIfRebuildRequired();
        try
        {
            return _storage.GetStorageStats();
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    public bool ShouldCompact(CompactOptions options)
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        ValidateCompactOptions(options);
        try
        {
            bool shouldCompact;
            using (var snapshot = _storage.OpenReadSnapshot())
            {
                var selectedSegmentIds = SelectSegmentsForCompaction(
                    snapshot.Manifest,
                    options,
                    CancellationToken.None);
                shouldCompact = selectedSegmentIds.Count > 0 &&
                    HasEstimatedCompactionCapacity(
                        snapshot.Manifest,
                        selectedSegmentIds,
                        options,
                        CancellationToken.None);
            }

            // A concurrent publication may have retired the briefly leased generation.
            ScheduleReclamationIfPending();
            return shouldCompact;
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    public Task<UpdateStats> Update(IDocumentSource upserts, ReadOnlySpan<DocumentKey> deletions)
    {
        return Update(upserts, deletions, CancellationToken.None);
    }

    public Task<UpdateStats> Update(
        IDocumentSource upserts,
        ReadOnlySpan<DocumentKey> deletions,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        var deletionArray = deletions.ToArray();
        var activity = BeginActivity(SiftActivity.Update);
        return Task.Run(() =>
        {
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _writerCancellation.Token);
            try
            {
                return UpdateCore(upserts, deletionArray, operationCancellation.Token);
            }
            catch (Exception exception) when (IsIndexCorruption(exception))
            {
                MarkRequiresRebuild(exception);
                throw;
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

        static uint[] MergeDeadOrdinals(uint[] existing, List<LiveDocumentKey> added)
        {
            var merged = new uint[existing.Length + added.Count];
            var existingIndex = 0;
            var addedIndex = 0;
            var mergedIndex = 0;
            while (existingIndex < existing.Length && addedIndex < added.Count)
            {
                if (existing[existingIndex] < added[addedIndex].Ordinal)
                {
                    merged[mergedIndex++] = existing[existingIndex++];
                }
                else
                {
                    merged[mergedIndex++] = added[addedIndex++].Ordinal;
                }
            }

            existing.AsSpan(existingIndex).CopyTo(merged.AsSpan(mergedIndex));
            mergedIndex += existing.Length - existingIndex;
            while (addedIndex < added.Count)
            {
                merged[mergedIndex++] = added[addedIndex++].Ordinal;
            }

            return merged;
        }

        var watch = Stopwatch.StartNew();
        using var _ = _storage.AcquireWriterLock(cancellationToken);
        var hadManifest = _storage.HasManifest;
        var existingManifest = hadManifest
            ? _storage.ReadManifest()
            : new CorpusManifest(Guid.NewGuid(), 0, []);

        var deletionSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deletion in deletions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = IndexBuilder.CanonicalizePath(deletion.Path);
            if (!deletionSet.Add(normalized))
            {
                throw new InvalidOperationException($"Duplicate deletion path in update batch: '{normalized}'.");
            }
        }

        var publishedSegments = new List<SegmentManifestEntry>();
        var attemptedSegmentIds = new List<ulong>();
        var deletedDocuments = 0;
        StreamedDocuments upsertLoad;
        try
        {
            var segmentSeed = (existingManifest.Generation + 1UL) ^ ((ulong)DateTime.UtcNow.Ticks << 1);
            upsertLoad = IndexBuilder.StreamDocuments(
                upserts,
                cancellationToken,
                _options,
                operationName: "update",
                forbiddenPaths: deletionSet,
                collectAcceptedPaths: true,
                preparePartition: partition => RemoveUnchangedDocuments(
                    existingManifest,
                    partition,
                    cancellationToken),
                partition =>
                {
                    var segmentId = segmentSeed + (ulong)publishedSegments.Count;
                    attemptedSegmentIds.Add(segmentId);
                    publishedSegments.Add(_storage.PublishSegment(
                        partition,
                        segmentId,
                        _options.MinimumFreeBytesAfterMutation,
                        cancellationToken));
                });

            var affectedPaths = new HashSet<string>(upsertLoad.AcceptedPaths, StringComparer.Ordinal);
            affectedPaths.UnionWith(deletionSet);
            var liveDocumentKeys = FindLiveDocumentKeys(
                existingManifest,
                affectedPaths,
                cancellationToken);
            foreach (var path in deletionSet)
            {
                if (liveDocumentKeys.ContainsKey(path))
                {
                    deletedDocuments++;
                }
            }

            var requiresManifestMigration = existingManifest.Segments.Any(
                static segment => segment.DeadContentBytes < 0);
            if (!hadManifest ||
                publishedSegments.Count > 0 ||
                liveDocumentKeys.Count > 0 ||
                requiresManifestMigration)
            {
                var newSegments = new List<SegmentManifestEntry>(existingManifest.Segments.Count + publishedSegments.Count);
                var newlyDeadBySegment = new Dictionary<ulong, List<LiveDocumentKey>>();

                // A rejected replacement never became durable, so keep the prior
                // searchable version until a valid upsert or explicit deletion arrives.
                foreach (var path in affectedPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (liveDocumentKeys.TryGetValue(path, out var current))
                    {
                        if (!newlyDeadBySegment.TryGetValue(current.SegmentId, out var newlyDead))
                        {
                            newlyDead = [];
                            newlyDeadBySegment.Add(current.SegmentId, newlyDead);
                        }

                        newlyDead.Add(current);
                    }
                }

                foreach (var segment in existingManifest.Segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Preserve both the entry and its dead-ordinal array when this
                    // batch did not replace or delete anything in the segment.
                    if (!newlyDeadBySegment.TryGetValue(segment.SegmentId, out var newlyDead))
                    {
                        newSegments.Add(segment);
                        continue;
                    }

                    newlyDead.Sort(static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
                    var deadContentBytes = segment.DeadContentBytes;
                    if (deadContentBytes >= 0)
                    {
                        foreach (var document in newlyDead)
                        {
                            deadContentBytes = checked(deadContentBytes + document.ContentLength);
                        }
                    }

                    newSegments.Add(segment with
                    {
                        DeadOrdinals = MergeDeadOrdinals(segment.DeadOrdinals, newlyDead),
                        DeadContentBytes = deadContentBytes
                    });
                }

                newSegments.AddRange(publishedSegments);

                var manifest = new CorpusManifest(existingManifest.CorpusId, existingManifest.Generation + 1UL, newSegments);
                _storage.PublishManifest(manifest);
            }
        }
        catch
        {
            _storage.CleanupFailedMutationUnderWriterLock(attemptedSegmentIds);
            throw;
        }

        watch.Stop();
        return new UpdateStats(
            SubmittedUpserts: upsertLoad.Submitted,
            UpsertedDocuments: upsertLoad.Accepted,
            SkippedUpserts: upsertLoad.Skipped,
            RejectedUpserts: upsertLoad.Rejected,
            DeletedDocuments: deletedDocuments,
            PublishedSegments: publishedSegments.Count,
            SubmittedBytes: upsertLoad.SubmittedBytes,
            ContentBytes: upsertLoad.AcceptedBytes,
            SkippedBytes: upsertLoad.SkippedBytes,
            RejectedBytes: upsertLoad.RejectedBytes,
            ElapsedMilliseconds: watch.ElapsedMilliseconds);
    }

    public Task<CompactStats> Compact(CompactOptions options)
    {
        return Compact(options, CancellationToken.None);
    }

    public Task<CompactStats> Compact(CompactOptions options, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        ValidateCompactOptions(options);
        var activity = BeginActivity(SiftActivity.Compact);
        return Task.Run(async () =>
        {
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _writerCancellation.Token);
            try
            {
                using var measurement = ManagedMemoryPeakMeasurement.Start();
                var stats = CompactCore(options, operationCancellation.Token);
                var peakManagedBytes = await measurement.CompleteAsync().ConfigureAwait(false);
                return stats with { PeakManagedBytes = peakManagedBytes };
            }
            catch (Exception exception) when (IsIndexCorruption(exception))
            {
                MarkRequiresRebuild(exception);
                throw;
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
        var selectedSegmentIds = SelectSegmentsForCompaction(
            manifest,
            options,
            cancellationToken);
        if (selectedSegmentIds.Count == 0)
        {
            watch.Stop();
            return new CompactStats(
                InputSegments: 0,
                OutputSegments: 0,
                MergedDocuments: 0,
                ContentBytes: 0,
                ElapsedMilliseconds: watch.ElapsedMilliseconds,
                PeakManagedBytes: 0,
                HasMoreWork: false);
        }

        _storage.EnsureReplacementAllowedUnderWriterLock();

        var segmentSeed = (manifest.Generation + 1UL) ^ ((ulong)DateTime.UtcNow.Ticks << 1);
        var attemptedSegmentIds = new List<ulong>();
        var newSegments = new List<SegmentManifestEntry>();
        CompactionPublication publication;
        try
        {
            publication = PublishCompactedSegments(
                manifest,
                selectedSegmentIds,
                segmentSeed,
                attemptedSegmentIds,
                newSegments,
                options,
                cancellationToken);

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
            manifest = newManifest;
        }
        catch
        {
            _storage.CleanupFailedMutationUnderWriterLock(attemptedSegmentIds);
            throw;
        }

        watch.Stop();
        return new CompactStats(
            InputSegments: selectedSegmentIds.Count,
            OutputSegments: newSegments.Count,
            MergedDocuments: publication.MergedDocuments,
            ContentBytes: publication.ContentBytes,
            ElapsedMilliseconds: watch.ElapsedMilliseconds,
            PeakManagedBytes: 0,
            HasMoreWork: SelectSegmentsForCompaction(
                manifest,
                options,
                CancellationToken.None).Count > 0);
    }

    public void Dispose()
    {
        lock (_activitySync)
        {
            if (_disposed != 0)
            {
                return;
            }

            Volatile.Write(ref _disposed, 1);
            _writerCancellation.Cancel();
        }

        _writersIdle.Wait();
        lock (_reclamationSync)
        {
            _reclamationCancellation.Cancel();
        }

        _reclamationIdle.Wait();
        _writerCancellation.Dispose();
        _writersIdle.Dispose();
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

        if (!double.IsFinite(options.DeadContentFractionThreshold) ||
            options.DeadContentFractionThreshold < 0d ||
            options.DeadContentFractionThreshold > 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "DeadContentFractionThreshold must be between zero and one.");
        }

        if (options.SmallSegmentMergeThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SmallSegmentMergeThreshold must be positive.");
        }

        if (options.MetadataBloatDeadOrdinalsThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MetadataBloatDeadOrdinalsThreshold must be positive.");
        }

        if (options.MaxInputSegmentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxInputSegmentBytes must be positive.");
        }

        if (options.MinimumFreeBytesAfterCompaction < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MinimumFreeBytesAfterCompaction cannot be negative.");
        }
    }

    private static void ValidateSiftOptions(SiftOptions options)
    {
        if (options.MaxDocumentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDocumentBytes must be positive.");
        }

        if (options.TargetSegmentDocumentCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "TargetSegmentDocumentCount must be positive.");
        }

        if (options.TargetSegmentContentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "TargetSegmentContentBytes must be positive.");
        }

        if (options.MaximumPhysicalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumPhysicalBytes must be positive.");
        }

        if (options.MinimumFreeBytesAfterMutation < 0 ||
            options.MinimumFreeBytesAfterMutation >= options.MaximumPhysicalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MinimumFreeBytesAfterMutation must be non-negative and smaller than MaximumPhysicalBytes.");
        }

        if (options.DirectScanScopeDocumentsThreshold < 0 ||
            options.DirectScanScopeBytesThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Direct-scan thresholds cannot be negative.");
        }

        if (double.IsNaN(options.DirectScanCandidateDensityThreshold) ||
            options.DirectScanCandidateDensityThreshold < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "DirectScanCandidateDensityThreshold cannot be negative or NaN.");
        }

        if (options.MaxSelectedNgrams <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSelectedNgrams must be positive.");
        }

        if (options.MaxMaterializedMatchBytes <= 0 ||
            options.MaxMaterializedContextBytes < 0 ||
            options.MaxMaterializedContextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Materialization limits are invalid.");
        }
    }

    private IDisposable BeginActivity(SiftActivity activity)
    {
        lock (_activitySync)
        {
            ThrowIfDisposed();
            if (++_activeWriters == 1)
            {
                Volatile.Write(ref _currentActivity, (int)activity);
                _writersIdle.Reset();
            }
        }

        return new ActivityScope(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private void ThrowIfRebuildRequired()
    {
        var cause = Volatile.Read(ref _rebuildCause);
        if (cause is not null)
        {
            throw new InvalidDataException(
                "The Sift index is corrupt or incomplete. Run a full Build to replace it.",
                cause);
        }
    }

    private void MarkRequiresRebuild(Exception exception)
    {
        if (IsIndexCorruption(exception))
        {
            Interlocked.CompareExchange(ref _rebuildCause, exception, null);
        }
    }

    private static bool IsIndexCorruption(Exception exception)
    {
        return exception is InvalidDataException or FileNotFoundException or DirectoryNotFoundException or EndOfStreamException;
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

            lock (owner._activitySync)
            {
                if (--owner._activeWriters == 0)
                {
                    Volatile.Write(ref owner._currentActivity, (int)SiftActivity.Idle);
                    owner._writersIdle.Set();
                }
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

        string[]? sortedPaths = null;
        foreach (var entry in manifest.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var segment = _storage.OpenSegment(entry.SegmentId, cancellationToken);
            var binarySearchComparisons = (long)affectedPaths.Count *
                (BitOperations.Log2((uint)Math.Max(segment.DocumentCount, 1)) + 1);
            if (binarySearchComparisons <= segment.DocumentCount)
            {
                // A small batch can target paths near the end of a large segment,
                // for which a merge scan would traverse nearly every document.
                foreach (var path in affectedPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!segment.TryFindDocumentOrdinal(path, out var foundOrdinal) ||
                        Array.BinarySearch(entry.DeadOrdinals, foundOrdinal) >= 0)
                    {
                        continue;
                    }

                    var document = segment.GetDocument(foundOrdinal);
                    live[path] = new LiveDocumentKey(
                        entry.SegmentId,
                        foundOrdinal,
                        document.ContentLength,
                        document.MTimeUtcTicks,
                        document.ContentHash64);
                }

                continue;
            }

            if (sortedPaths is null)
            {
                sortedPaths = affectedPaths.ToArray();
                Array.Sort(sortedPaths, StringComparer.Ordinal);
            }

            // Both inputs are ordinal-sorted, so one merge pass replaces a
            // binary search for every path in a large update batch.
            var pathIndex = 0;
            var ordinal = 0u;
            var deadIndex = 0;
            var comparisons = 0;
            while (pathIndex < sortedPaths.Length && ordinal < segment.DocumentCount)
            {
                if ((comparisons++ & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var document = segment.GetDocument(ordinal);
                var comparison = segment.ComparePathOrdinal(document, sortedPaths[pathIndex]);
                if (comparison < 0)
                {
                    ordinal++;
                    continue;
                }

                if (comparison > 0)
                {
                    pathIndex++;
                    continue;
                }

                while (deadIndex < entry.DeadOrdinals.Length && entry.DeadOrdinals[deadIndex] < ordinal)
                {
                    deadIndex++;
                }

                if (deadIndex >= entry.DeadOrdinals.Length || entry.DeadOrdinals[deadIndex] != ordinal)
                {
                    live[sortedPaths[pathIndex]] = new LiveDocumentKey(
                        entry.SegmentId,
                        ordinal,
                        document.ContentLength,
                        document.MTimeUtcTicks,
                        document.ContentHash64);
                }

                ordinal++;
                pathIndex++;
            }
        }

        return live;
    }

    private void RemoveUnchangedDocuments(
        CorpusManifest manifest,
        List<StoredDocument> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0 || manifest.Segments.Count == 0)
        {
            return;
        }

        var byPath = documents.ToDictionary(static document => document.Path, StringComparer.Ordinal);
        var paths = byPath.Keys.ToHashSet(StringComparer.Ordinal);
        var current = FindLiveDocumentKeys(manifest, paths, cancellationToken);
        var unchangedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in current.GroupBy(static pair => pair.Value.SegmentId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var segment = _storage.OpenSegment(group.Key, cancellationToken);
            foreach (var pair in group)
            {
                var incoming = byPath[pair.Key];
                var indexed = pair.Value;
                if (indexed.ContentLength != incoming.Content.Length ||
                    indexed.ContentHash64 != incoming.ContentHash64 ||
                    indexed.MTimeUtcTicks != incoming.MTimeUtcTicks)
                {
                    continue;
                }

                // The persisted hash is only a fast rejection filter; byte equality
                // is required before suppressing a replacement.
                var indexedDocument = segment.GetDocument(indexed.Ordinal);
                if (segment.GetContentBytes(indexedDocument).SequenceEqual(incoming.Content.Span))
                {
                    unchangedPaths.Add(pair.Key);
                }
            }
        }

        documents.RemoveAll(document => unchangedPaths.Contains(document.Path));
    }

    private CompactionPublication PublishCompactedSegments(
        CorpusManifest manifest,
        HashSet<ulong> selectedSegmentIds,
        ulong segmentSeed,
        List<ulong> attemptedSegmentIds,
        List<SegmentManifestEntry> published,
        CompactOptions options,
        CancellationToken cancellationToken)
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

                var cursor = new CompactionCursor(
                    _storage.OpenSegment(entry.SegmentId, cancellationToken),
                    entry.DeadOrdinals);
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
                queue.Enqueue(cursors[i], new CompactionPriority(cursors[i].Current));
            }

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
                    var segmentId = segmentSeed + (ulong)published.Count;
                    attemptedSegmentIds.Add(segmentId);
                    published.Add(_storage.PublishSegment(
                        currentPartition,
                        segmentId,
                        options.MinimumFreeBytesAfterCompaction,
                        cancellationToken));
                    currentPartition = new List<CompactionDocument>();
                    currentBytes = 0;
                }

                currentPartition.Add(next);
                currentBytes += next.ContentLength;
                mergedDocuments++;
                contentBytes += next.ContentLength;

                if (cursor.MoveNext())
                {
                    queue.Enqueue(cursor, new CompactionPriority(cursor.Current));
                }
            }

            if (currentPartition.Count > 0)
            {
                var segmentId = segmentSeed + (ulong)published.Count;
                attemptedSegmentIds.Add(segmentId);
                published.Add(_storage.PublishSegment(
                    currentPartition,
                    segmentId,
                    options.MinimumFreeBytesAfterCompaction,
                    cancellationToken));
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

    private HashSet<ulong> SelectSegmentsForCompaction(
        CorpusManifest manifest,
        CompactOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = manifest.Segments
            .Select(entry => new
            {
                Entry = entry,
                SegmentBytes = _storage.GetSegmentBytes(entry.SegmentId),
                DeadFraction = entry.DocumentCount == 0 ? 0d : (double)entry.DeadOrdinals.Length / entry.DocumentCount,
                DeadContentFraction = entry.ContentBytes == 0
                    ? 0d
                    : (double)GetDeadContentBytes(
                        entry,
                        currentLegacyKeys: null,
                        cancellationToken: cancellationToken) / entry.ContentBytes,
                HasMetadataBloat = entry.DeadOrdinals.Length >= options.MetadataBloatDeadOrdinalsThreshold
            })
            .OrderByDescending(item => item.DeadContentFraction)
            .ThenByDescending(item => item.DeadFraction)
            .ThenByDescending(item => item.HasMetadataBloat)
            .ThenBy(item => item.Entry.LiveDocumentCount)
            .ThenBy(item => item.Entry.SegmentId)
            .ToArray();

        var selected = new HashSet<ulong>();
        long selectedSegmentBytes = 0;
        foreach (var item in ordered.Where(item =>
                     item.DeadFraction >= options.DeadFractionThreshold ||
                     item.DeadContentFraction >= options.DeadContentFractionThreshold ||
                     item.HasMetadataBloat))
        {
            if (selected.Count >= options.MaxMergedSegments)
            {
                break;
            }

            if (selected.Count > 0 &&
                item.SegmentBytes > options.MaxInputSegmentBytes - selectedSegmentBytes)
            {
                continue;
            }

            selected.Add(item.Entry.SegmentId);
            selectedSegmentBytes += item.SegmentBytes;
        }

        if (selected.Count > 0 || manifest.Segments.Count <= options.SmallSegmentMergeThreshold)
        {
            return selected;
        }

        long mergedContentBytes = 0;
        var mergedDocuments = 0;
        foreach (var item in ordered.OrderBy(item => item.Entry.ContentBytes)
                     .ThenBy(item => item.Entry.LiveDocumentCount)
                     .ThenBy(item => item.Entry.SegmentId))
        {
            if (selected.Count >= options.MaxMergedSegments ||
                item.Entry.LiveDocumentCount == 0 ||
                item.SegmentBytes > options.MaxInputSegmentBytes - selectedSegmentBytes ||
                item.Entry.ContentBytes > _options.TargetSegmentContentBytes - mergedContentBytes ||
                item.Entry.LiveDocumentCount > _options.TargetSegmentDocumentCount - mergedDocuments)
            {
                continue;
            }

            selected.Add(item.Entry.SegmentId);
            selectedSegmentBytes += item.SegmentBytes;
            mergedContentBytes += item.Entry.ContentBytes;
            mergedDocuments += item.Entry.LiveDocumentCount;
        }

        if (selected.Count < 2)
        {
            selected.Clear();
        }

        return selected;
    }

    private bool HasEstimatedCompactionCapacity(
        CorpusManifest manifest,
        IReadOnlySet<ulong> selectedSegmentIds,
        CompactOptions options,
        CancellationToken cancellationToken)
    {
        var availableBytes = _storage.GetAvailableBytes();
        if (availableBytes is null)
        {
            return true;
        }

        long estimatedReplacementBytes = 0;
        foreach (var entry in manifest.Segments)
        {
            if (!selectedSegmentIds.Contains(entry.SegmentId) || entry.LiveDocumentCount == 0)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var segmentBytes = _storage.GetSegmentBytes(entry.SegmentId);
            var liveContentBytes = entry.ContentBytes - GetDeadContentBytes(
                entry,
                currentLegacyKeys: null,
                cancellationToken);
            var proportionalBytes = entry.ContentBytes == 0
                ? segmentBytes
                : (long)Math.Ceiling((double)segmentBytes * liveContentBytes / entry.ContentBytes);
            estimatedReplacementBytes = SaturatingAdd(
                estimatedReplacementBytes,
                SaturatingAdd(proportionalBytes, EstimatedReplacementPackOverheadBytes));
        }

        return estimatedReplacementBytes <=
            availableBytes.Value - Math.Min(availableBytes.Value, options.MinimumFreeBytesAfterCompaction);

        static long SaturatingAdd(long left, long right)
        {
            return right > long.MaxValue - left ? long.MaxValue : left + right;
        }
    }

    private long GetDeadContentBytes(
        SegmentManifestEntry segment,
        ISet<LegacyMaintenanceKey>? currentLegacyKeys,
        CancellationToken cancellationToken)
    {
        if (segment.DeadOrdinals.Length == 0)
        {
            return 0;
        }

        if (segment.DeadContentBytes >= 0)
        {
            return segment.DeadContentBytes;
        }

        var key = new LegacyMaintenanceKey(
            segment.SegmentId,
            segment.DocumentCount,
            segment.ContentBytes,
            segment.DeadOrdinals.Length);
        currentLegacyKeys?.Add(key);
        if (_legacyDeadContentBytes.TryGetValue(key, out var cachedBytes))
        {
            return cachedBytes;
        }

        long deadContentBytes = 0;
        using var mappedSegment = _storage.OpenSegment(
            segment.SegmentId,
            cancellationToken);
        foreach (var deadOrdinal in segment.DeadOrdinals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            deadContentBytes = checked(
                deadContentBytes +
                mappedSegment.GetDocument(deadOrdinal).ContentLength);
        }

        return _legacyDeadContentBytes.GetOrAdd(key, deadContentBytes);
    }

    private readonly record struct LiveDocumentKey(
        ulong SegmentId,
        uint Ordinal,
        int ContentLength,
        long MTimeUtcTicks,
        ulong ContentHash64);

    private readonly record struct LegacyMaintenanceKey(
        ulong SegmentId,
        int DocumentCount,
        long ContentBytes,
        int DeadDocumentCount);

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

    private readonly record struct CompactionPriority(CompactionDocument Document);

    private sealed class CompactionPriorityComparer : IComparer<CompactionPriority>
    {
        public static CompactionPriorityComparer Instance { get; } = new();

        public int Compare(CompactionPriority left, CompactionPriority right)
        {
            // Segment lookup uses UTF-16 ordinal ordering, which differs from UTF-8 byte order.
            return MappedSegment.ComparePathOrdinal(
                left.Document.GetPathUtf8(),
                right.Document.GetPathUtf8());
        }
    }

    private sealed class CompactionCursor : IDisposable
    {
        private readonly uint[] _deadOrdinals;
        private CompactionDocument? _current;
        private int _deadIndex;
        private uint _nextOrdinal;

        public CompactionCursor(MappedSegment segment, uint[] deadOrdinals)
        {
            Segment = segment;
            _deadOrdinals = deadOrdinals;
        }

        public MappedSegment Segment { get; }
        public CompactionDocument Current =>
            _current ?? throw new InvalidOperationException("The cursor has not advanced to a document.");

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

                _current = new CompactionDocument(Segment, Segment.GetDocument(ordinal));
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            Segment.Dispose();
        }
    }

}

internal readonly record struct CompactionPublication(
    List<SegmentManifestEntry> Segments,
    int MergedDocuments,
    long ContentBytes);

internal readonly record struct CompactionDocument(
    MappedSegment Segment,
    SegmentDocument Document) : ISegmentBuildDocument
{
    public int LineCount => Document.LineCount;
    public ushort Flags => Document.Flags;
    public long MTimeUtcTicks => Document.MTimeUtcTicks;
    public ulong ContentHash64 => Document.ContentHash64;
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

