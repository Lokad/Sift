namespace Lokad.Sift.Storage;

internal sealed partial class CorpusStorage
{
    private readonly record struct PhysicalSegment(
        SegmentId SegmentId,
        string DirectoryPath,
        long Bytes);

    private readonly record struct GenerationLeaseData(
        CorpusId CorpusId,
        StorageGeneration Generation,
        SegmentId[] SegmentIds);

    private readonly record struct LeaseProtection(
        HashSet<SegmentId> ProtectedSegmentIds,
        int LeasedGenerationCount,
        StorageGeneration? OldestLeasedGeneration,
        bool IsBlocked);

    private enum GenerationLeaseInspection
    {
        Observe,
        Reclaim
    }

    private sealed class CapacityBudget(long physicalBytes, long? availableBytes)
    {
        public long PhysicalBytes { get; private set; } = physicalBytes;
        public long? AvailableBytes { get; private set; } = availableBytes;

        public void Debit(long bytes)
        {
            PhysicalBytes = checked(PhysicalBytes + bytes);
            if (AvailableBytes is { } available)
            {
                AvailableBytes = Math.Max(0, available - bytes);
            }
        }

        public void Credit(long bytes)
        {
            PhysicalBytes = Math.Max(0, PhysicalBytes - bytes);
            if (AvailableBytes is { } available)
            {
                AvailableBytes = available > long.MaxValue - bytes
                    ? long.MaxValue
                    : available + bytes;
            }
        }
    }

    private sealed class WriteSession(
        CorpusStorage storage,
        IDisposable writerLock,
        IDisposable indexingWorkspaceLease) : IStorageWriteSession
    {
        private IDisposable? _writerLock = writerLock;
        private IDisposable? _indexingWorkspaceLease = indexingWorkspaceLease;
        private CorpusManifest? _currentManifest;
        private bool _currentManifestLoaded;

        public CorpusManifest? CurrentManifest
        {
            get
            {
                if (!_currentManifestLoaded)
                {
                    _currentManifest = storage.HasManifest ? storage.RefreshManifest() : null;
                    _currentManifestLoaded = true;
                }

                return _currentManifest;
            }
        }

        public SegmentManifestEntry PublishSegment<TDocument>(
            IReadOnlyList<TDocument> documents,
            SegmentId segmentId,
            long minimumFreeBytesAfterPublication,
            CancellationToken cancellationToken)
            where TDocument : ISegmentBuildDocument
        {
            return storage.PublishSegment(
                documents,
                segmentId,
                minimumFreeBytesAfterPublication,
                cancellationToken);
        }

        public void PublishManifest(CorpusManifest manifest, long minimumFreeBytesAfterPublication)
        {
            storage.PublishManifest(manifest, minimumFreeBytesAfterPublication);
            _currentManifest = storage.ReadManifest();
            _currentManifestLoaded = true;
        }

        public bool CanReplaceSegments()
        {
            var canReplace = storage.CanReplaceSegmentsUnderWriterLock();
            ReloadCurrentManifestFromCache();
            return canReplace;
        }

        public void EnsureReplacementAllowed()
        {
            storage.EnsureReplacementAllowedUnderWriterLock();
            ReloadCurrentManifestFromCache();
        }

        public void PrepareFullBuild(bool discardCorruptState)
        {
            storage.PrepareFullBuildUnderWriterLock(discardCorruptState);
            ReloadCurrentManifestFromCache();
        }

        public void CleanupFailedMutation(IReadOnlyCollection<SegmentId> attemptedSegmentIds) =>
            storage.CleanupFailedMutationUnderWriterLock(attemptedSegmentIds);

        public void Dispose()
        {
            Interlocked.Exchange(ref _indexingWorkspaceLease, null)?.Dispose();
            Interlocked.Exchange(ref _writerLock, null)?.Dispose();
        }

        private void ReloadCurrentManifestFromCache()
        {
            _currentManifest = storage.HasManifest ? storage.ReadManifest() : null;
            _currentManifestLoaded = true;
        }
    }

    private sealed class WriterLease(CorpusStorage owner, FileStream stream) : IDisposable
    {
        private CorpusStorage? _owner = owner;
        private FileStream? _stream = stream;

        public void Dispose()
        {
            var currentOwner = Interlocked.Exchange(ref _owner, null);
            if (currentOwner is null)
            {
                return;
            }

            currentOwner.EndWriterLease();
            Interlocked.Exchange(ref _stream, null)?.Dispose();
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
