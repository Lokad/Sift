using Lokad.Sift;

namespace Lokad.Sift.Storage;

internal sealed class InMemorySiftStorage : ISiftStorage
{
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<ulong, byte[]> _segments = new();
    private readonly Dictionary<ulong, int> _snapshotSegmentReferences = new();
    private readonly Dictionary<ulong, int> _snapshotGenerationReferences = new();
    private CorpusManifest? _manifest;
    private bool _reclamationPending;

    public bool HasManifest
    {
        get
        {
            lock (_stateLock)
            {
                return _manifest is not null;
            }
        }
    }

    public IDisposable AcquireWriterLock()
    {
        return AcquireWriterLock(CancellationToken.None);
    }

    public IDisposable AcquireWriterLock(CancellationToken cancellationToken)
    {
        _writerGate.Wait(cancellationToken);
        return new Releaser(_writerGate);
    }

    public SegmentManifestEntry PublishSegment<TDocument>(
        IReadOnlyList<TDocument> documents,
        ulong segmentId,
        long minimumFreeBytesAfterPublication,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument
    {
        var (bytes, contentBytes) = SegmentWriter.WriteToBytes(segmentId, documents, cancellationToken);
        using var _ = new MappedSegment(bytes, validateChecksums: true);
        lock (_stateLock)
        {
            _reclamationPending = true;
            if (!_segments.TryAdd(segmentId, bytes))
            {
                throw new IOException($"Sift segment id collision for '{segmentId:X16}'.");
            }
        }

        return new SegmentManifestEntry(segmentId, documents.Count, contentBytes, [], DeadContentBytes: 0);
    }

    public void PublishManifest(CorpusManifest manifest)
    {
        lock (_stateLock)
        {
            _manifest = CloneManifest(manifest);
            _ = ReclaimUnreferencedSegmentsUnderWriterLock();
        }
    }

    public CorpusManifest ReadManifest()
    {
        lock (_stateLock)
        {
            return _manifest is not null
                ? CloneManifest(_manifest)
                : throw new FileNotFoundException("Sift manifest is not present in memory-backed storage.");
        }
    }

    public MappedSegment OpenSegment(ulong segmentId)
    {
        return OpenSegment(segmentId, CancellationToken.None);
    }

    public MappedSegment OpenSegment(ulong segmentId, CancellationToken cancellationToken)
    {
        byte[] bytes;
        lock (_stateLock)
        {
            if (!_segments.TryGetValue(segmentId, out var storedBytes))
            {
                throw new FileNotFoundException($"Missing in-memory Sift segment '{segmentId:X16}'.");
            }

            bytes = storedBytes;
        }

        // Published in-memory packs were checksummed immediately before insertion.
        var segment = new MappedSegment(
            bytes,
            validateChecksums: false,
            cancellationToken);
        if (segment.SegmentId == segmentId)
        {
            return segment;
        }

        var actualSegmentId = segment.SegmentId;
        segment.Dispose();
        throw new InvalidDataException(
            $"Sift segment '{segmentId:X16}' contains id '{actualSegmentId:X16}'.");
    }

    public void ValidateSegment(ulong segmentId, CancellationToken cancellationToken)
    {
        byte[] bytes;
        lock (_stateLock)
        {
            if (!_segments.TryGetValue(segmentId, out var storedBytes))
            {
                throw new FileNotFoundException($"Missing in-memory Sift segment '{segmentId:X16}'.");
            }

            bytes = storedBytes;
        }

        using var segment = new MappedSegment(
            bytes,
            validateChecksums: true,
            cancellationToken);
        if (segment.SegmentId != segmentId)
        {
            throw new InvalidDataException(
                $"Sift segment '{segmentId:X16}' contains id '{segment.SegmentId:X16}'.");
        }
    }

    public long GetSegmentBytes(ulong segmentId)
    {
        lock (_stateLock)
        {
            return _segments.TryGetValue(segmentId, out var bytes)
                ? bytes.Length
                : throw new FileNotFoundException($"Missing in-memory Sift segment '{segmentId:X16}'.");
        }
    }

    public long? GetAvailableBytes()
    {
        return null;
    }

    bool ISiftStorage.IsReclamationPending
    {
        get
        {
            lock (_stateLock)
            {
                return _reclamationPending;
            }
        }
    }

    void ISiftStorage.EnsureSnapshotMetadata()
    {
    }

    StorageReadSnapshot ISiftStorage.OpenReadSnapshot()
    {
        lock (_stateLock)
        {
            var manifest = _manifest is not null
                ? CloneManifest(_manifest)
                : new CorpusManifest(Guid.Empty, 0, []);
            if (manifest.Segments.Count == 0)
            {
                return new StorageReadSnapshot(manifest, EmptyDisposable.Instance);
            }

            var segmentIds = manifest.Segments.Select(static segment => segment.SegmentId).ToArray();
            for (var i = 0; i < segmentIds.Length; i++)
            {
                _snapshotSegmentReferences.TryGetValue(segmentIds[i], out var references);
                _snapshotSegmentReferences[segmentIds[i]] = references + 1;
            }

            _snapshotGenerationReferences.TryGetValue(manifest.Generation, out var generationReferences);
            _snapshotGenerationReferences[manifest.Generation] = generationReferences + 1;
            return new StorageReadSnapshot(
                manifest,
                new SnapshotLease(this, manifest.Generation, segmentIds));
        }
    }

    StorageStats ISiftStorage.GetStorageStats()
    {
        lock (_stateLock)
        {
            var reclamation = ReclaimUnreferencedSegmentsUnderWriterLock();
            return GetStorageStatsUnderWriterLock(reclamation);
        }
    }

    void ISiftStorage.ReclaimUnreferencedSegments(CancellationToken cancellationToken)
    {
        using var writerLock = AcquireWriterLock(cancellationToken);
        _ = ReclaimUnreferencedSegmentsUnderWriterLock();
    }

    void ISiftStorage.EnsureReplacementAllowedUnderWriterLock()
    {
        EnsureReplacementAllowedUnderWriterLock();
    }

    bool ISiftStorage.CanReplaceSegmentsUnderWriterLock()
    {
        return ReclaimUnreferencedSegmentsUnderWriterLock().DeferredSegmentCount == 0;
    }

    void ISiftStorage.PrepareFullBuildUnderWriterLock(bool discardCorruptState)
    {
        lock (_stateLock)
        {
            if (!discardCorruptState)
            {
                EnsureReplacementAllowedUnderWriterLock();
                return;
            }

            _manifest = null;
            _segments.Clear();
            _snapshotSegmentReferences.Clear();
            _snapshotGenerationReferences.Clear();
            _reclamationPending = false;
        }
    }

    void ISiftStorage.CleanupFailedMutationUnderWriterLock(IReadOnlyCollection<ulong> attemptedSegmentIds)
    {
        lock (_stateLock)
        {
            var referencedSegmentIds = _manifest?.Segments
                .Select(static segment => segment.SegmentId)
                .ToHashSet() ?? [];
            foreach (var segmentId in attemptedSegmentIds)
            {
                if (!referencedSegmentIds.Contains(segmentId))
                {
                    _segments.Remove(segmentId);
                }
            }

            _reclamationPending = _segments.Keys.Any(segmentId => !referencedSegmentIds.Contains(segmentId));
        }
    }

    private StorageStats GetStorageStatsUnderWriterLock(SegmentReclamationStats reclamation)
    {
        var manifestSegmentIds = _manifest?.Segments.Select(static segment => segment.SegmentId).ToHashSet() ?? [];
        var physicalSegmentBytes = _segments.Values.Sum(static bytes => (long)bytes.Length);
        long manifestSegmentBytes = 0;
        var manifestSegmentCount = 0;
        foreach (var segmentId in manifestSegmentIds)
        {
            if (_segments.TryGetValue(segmentId, out var bytes))
            {
                manifestSegmentCount++;
                manifestSegmentBytes += bytes.Length;
            }
        }

        return new StorageStats(
            ManifestSegmentCount: manifestSegmentCount,
            ManifestSegmentBytes: manifestSegmentBytes,
            PhysicalSegmentCount: _segments.Count,
            PhysicalSegmentBytes: physicalSegmentBytes,
            RetiredSegmentCount: _segments.Count - manifestSegmentCount,
            RetiredSegmentBytes: physicalSegmentBytes - manifestSegmentBytes,
            TemporaryBytes: 0,
            CorpusPhysicalBytes: physicalSegmentBytes,
            MaximumPhysicalBytes: long.MaxValue,
            AvailableBytes: null,
            LeasedGenerationCount: reclamation.LeasedGenerationCount,
            OldestLeasedGeneration: reclamation.OldestLeasedGeneration,
            SnapshotDeferredSegmentCount: reclamation.DeferredSegmentCount,
            SnapshotDeferredSegmentBytes: reclamation.DeferredSegmentBytes,
            PendingDeletionSegmentCount: reclamation.FailedSegmentCount,
            PendingDeletionSegmentBytes: reclamation.FailedSegmentBytes,
            BlockedReclamationSegmentCount: reclamation.BlockedSegmentCount,
            BlockedReclamationSegmentBytes: reclamation.BlockedSegmentBytes,
            IsReclamationBlocked: reclamation.IsBlocked,
            ReclamationFailureCount: 0,
            LastReclamationFailure: null);
    }

    private SegmentReclamationStats ReclaimUnreferencedSegmentsUnderWriterLock()
    {
        lock (_stateLock)
        {
            return ReclaimUnreferencedSegmentsCore();
        }
    }

    private SegmentReclamationStats ReclaimUnreferencedSegmentsCore()
    {
        if (_manifest is null)
        {
            return default;
        }

        var manifestSegmentIds = _manifest.Segments.Select(static segment => segment.SegmentId).ToHashSet();
        var reclaimedSegmentCount = 0;
        long reclaimedSegmentBytes = 0;
        var deferredSegmentCount = 0;
        long deferredSegmentBytes = 0;
        foreach (var segment in _segments.ToArray())
        {
            if (manifestSegmentIds.Contains(segment.Key))
            {
                continue;
            }

            if (_snapshotSegmentReferences.ContainsKey(segment.Key))
            {
                deferredSegmentCount++;
                deferredSegmentBytes += segment.Value.Length;
                continue;
            }

            _segments.Remove(segment.Key);
            reclaimedSegmentCount++;
            reclaimedSegmentBytes += segment.Value.Length;
        }

        var reclamation = new SegmentReclamationStats(
            reclaimedSegmentCount,
            reclaimedSegmentBytes,
            deferredSegmentCount,
            deferredSegmentBytes,
            FailedSegmentCount: 0,
            FailedSegmentBytes: 0,
            BlockedSegmentCount: 0,
            BlockedSegmentBytes: 0,
            IsBlocked: false,
            LeasedGenerationCount: _snapshotGenerationReferences.Keys.Count(generation => generation != _manifest.Generation),
            OldestLeasedGeneration: _snapshotGenerationReferences.Keys
                .Where(generation => generation != _manifest.Generation)
                .Select(static generation => (ulong?)generation)
                .Min());
        _reclamationPending =
            reclamation.DeferredSegmentCount > 0 ||
            reclamation.LeasedGenerationCount > 0;
        return reclamation;
    }

    private void EnsureReplacementAllowedUnderWriterLock()
    {
        var reclamation = ReclaimUnreferencedSegmentsUnderWriterLock();
        if (reclamation.DeferredSegmentCount > 0)
        {
            throw new InvalidOperationException(
                $"Cannot replace Sift segments while {reclamation.DeferredSegmentCount} retired segment(s) " +
                $"({reclamation.DeferredSegmentBytes} bytes) remain leased by older snapshots.");
        }
    }

    private static CorpusManifest CloneManifest(CorpusManifest manifest)
    {
        var segments = new SegmentManifestEntry[manifest.Segments.Count];
        for (var i = 0; i < manifest.Segments.Count; i++)
        {
            var segment = manifest.Segments[i];
            segments[i] = segment with { DeadOrdinals = [.. segment.DeadOrdinals] };
        }

        return new CorpusManifest(
            manifest.CorpusId,
            manifest.ContentRevision,
            manifest.Generation,
            [.. manifest.CommitMetadata],
            segments);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }

    private sealed class SnapshotLease(InMemorySiftStorage owner, ulong generation, ulong[] segmentIds) : IDisposable
    {
        private InMemorySiftStorage? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            lock (owner._stateLock)
            {
                for (var i = 0; i < segmentIds.Length; i++)
                {
                    var references = owner._snapshotSegmentReferences[segmentIds[i]] - 1;
                    if (references == 0)
                    {
                        owner._snapshotSegmentReferences.Remove(segmentIds[i]);
                    }
                    else
                    {
                        owner._snapshotSegmentReferences[segmentIds[i]] = references;
                    }
                }

                var generationReferences = owner._snapshotGenerationReferences[generation] - 1;
                if (generationReferences == 0)
                {
                    owner._snapshotGenerationReferences.Remove(generation);
                }
                else
                {
                    owner._snapshotGenerationReferences[generation] = generationReferences;
                }
            }
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
