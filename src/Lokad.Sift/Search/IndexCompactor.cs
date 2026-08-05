using System.Collections.Concurrent;
using System.Diagnostics;
using Lokad.Sift.Indexer;
using Lokad.Sift.Storage;

namespace Lokad.Sift;

internal sealed class IndexCompactor
{
    private const long EstimatedReplacementPackOverheadBytes = 64L * 1024;
    private readonly ISiftStorage _storage;
    private readonly SiftOptions _options;
    private readonly ConcurrentDictionary<LegacyMaintenanceKey, long> _legacyDeadContentBytes = new();

    public IndexCompactor(ISiftStorage storage, SiftOptions options)
    {
        _storage = storage;
        _options = options;
    }

    public MaintenanceStats GetMaintenanceStats()
    {
        using var snapshot = _storage.OpenReadSnapshot();
        var manifest = snapshot.Manifest;
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
        return new MaintenanceStats(new MaintenanceStatsData
        {
            Generation = manifest.Generation,
            SegmentCount = manifest.Segments.Count,
            TotalDocuments = totalDocuments,
            LiveDocuments = liveDocuments,
            DeadDocuments = deadDocuments,
            DeadDocumentFraction = deadDocumentFraction,
            DeadContentFraction = deadContentFraction,
            ContentBytes = contentBytes,
            EstimatedLiveContentBytes = contentBytes - reclaimableContentBytes,
            EstimatedReclaimableContentBytes = reclaimableContentBytes,
            DeadOrdinalEntries = deadDocuments,
            SegmentsWithDeletions = segmentsWithDeletions,
            SmallSegmentCount = smallSegmentCount
        });
    }

    public bool ShouldCompact(
        CompactOptions options,
        Action ensureIndexUsable)
    {
        using var writeSession = _storage.AcquireWriteSession(CancellationToken.None);
        ensureIndexUsable();
        if (!_storage.HasManifest || !writeSession.CanReplaceSegments())
        {
            return false;
        }

        return HasActionableWork(
            writeSession.CurrentManifest ??
                throw new InvalidDataException("The Sift manifest disappeared while the writer lock was held."),
            options,
            CancellationToken.None);
    }

    public CompactStats Compact(
        CompactOptions options,
        Action ensureIndexUsable,
        Action resetValidation,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var writeSession = _storage.AcquireWriteSession(cancellationToken);
        ensureIndexUsable();
        var manifest = writeSession.CurrentManifest ?? new CorpusManifest(Guid.NewGuid(), 0, []);
        var selectedSegmentIds = SelectSegments(
            manifest,
            options,
            cancellationToken);
        if (selectedSegmentIds.Count == 0)
        {
            watch.Stop();
            return new CompactStats(new CompactStatsData
            {
                InputSegments = 0,
                OutputSegments = 0,
                MergedDocuments = 0,
                ContentBytes = 0,
                ElapsedMilliseconds = watch.ElapsedMilliseconds,
                PeakManagedBytes = 0,
                HasMoreWork = false
            });
        }

        writeSession.EnsureReplacementAllowed();
        var segmentSeed = (manifest.Generation + 1UL) ^ ((ulong)DateTime.UtcNow.Ticks << 1);
        var attemptedSegmentIds = new List<ulong>();
        var newSegments = new List<SegmentManifestEntry>();
        CompactionPublication publication;
        try
        {
            publication = PublishSegments(
                writeSession,
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

            var newManifest = new CorpusManifest(
                manifest.CorpusId,
                manifest.ContentRevision,
                manifest.Generation + 1UL,
                [.. manifest.CommitMetadata],
                mergedSegments);
            resetValidation();
            writeSession.PublishManifest(newManifest);
            resetValidation();
            manifest = newManifest;
        }
        catch
        {
            writeSession.CleanupFailedMutation(attemptedSegmentIds);
            throw;
        }

        watch.Stop();
        return new CompactStats(new CompactStatsData
        {
            InputSegments = selectedSegmentIds.Count,
            OutputSegments = newSegments.Count,
            MergedDocuments = publication.MergedDocuments,
            ContentBytes = publication.ContentBytes,
            ElapsedMilliseconds = watch.ElapsedMilliseconds,
            PeakManagedBytes = 0,
            HasMoreWork = writeSession.CanReplaceSegments() &&
                HasActionableWork(
                    manifest,
                    options,
                    CancellationToken.None)
        });
    }

    public static void ValidateOptions(CompactOptions options)
    {
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

    private bool HasActionableWork(
        CorpusManifest manifest,
        CompactOptions options,
        CancellationToken cancellationToken)
    {
        var selectedSegmentIds = SelectSegments(
            manifest,
            options,
            cancellationToken);
        return selectedSegmentIds.Count > 0 &&
               HasEstimatedCapacity(
                   manifest,
                   selectedSegmentIds,
                   options,
                   cancellationToken);
    }

    private CompactionPublication PublishSegments(
        IStorageWriteSession writeSession,
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
                    (currentPartition.Count >= _options.TargetSegmentDocumentCount ||
                     currentBytes + next.ContentLength > _options.TargetSegmentContentBytes))
                {
                    var segmentId = segmentSeed + (ulong)published.Count;
                    attemptedSegmentIds.Add(segmentId);
                    published.Add(writeSession.PublishSegment(
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
                published.Add(writeSession.PublishSegment(
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

    private HashSet<ulong> SelectSegments(
        CorpusManifest manifest,
        CompactOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = manifest.Segments
            .Select(entry => new
            {
                Entry = entry,
                DeadFraction = entry.DocumentCount == 0
                    ? 0d
                    : (double)entry.DeadOrdinals.Length / entry.DocumentCount,
                DeadContentFraction = entry.ContentBytes == 0
                    ? 0d
                    : (double)GetDeadContentBytes(
                        entry,
                        currentLegacyKeys: null,
                        cancellationToken) / entry.ContentBytes,
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

            var segmentBytes = _storage.GetSegmentBytes(item.Entry.SegmentId);
            if (selected.Count > 0 &&
                segmentBytes > options.MaxInputSegmentBytes - selectedSegmentBytes)
            {
                continue;
            }

            selected.Add(item.Entry.SegmentId);
            selectedSegmentBytes += segmentBytes;
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
                item.Entry.ContentBytes > _options.TargetSegmentContentBytes - mergedContentBytes ||
                item.Entry.LiveDocumentCount > _options.TargetSegmentDocumentCount - mergedDocuments)
            {
                continue;
            }

            var segmentBytes = _storage.GetSegmentBytes(item.Entry.SegmentId);
            if (segmentBytes > options.MaxInputSegmentBytes - selectedSegmentBytes)
            {
                continue;
            }

            selected.Add(item.Entry.SegmentId);
            selectedSegmentBytes += segmentBytes;
            mergedContentBytes += item.Entry.ContentBytes;
            mergedDocuments += item.Entry.LiveDocumentCount;
        }

        if (selected.Count < 2)
        {
            selected.Clear();
        }

        return selected;
    }

    private bool HasEstimatedCapacity(
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

    private readonly record struct LegacyMaintenanceKey(
        ulong SegmentId,
        int DocumentCount,
        long ContentBytes,
        int DeadDocumentCount);

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
