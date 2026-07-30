namespace Lokad.Sift.Storage;

public interface ISiftStorage
{
    bool HasManifest { get; }
    bool IsReclamationPending { get; }
    IDisposable AcquireWriterLock(CancellationToken cancellationToken = default);
    BuildPublication PublishFullBuild(IReadOnlyList<IReadOnlyList<StoredDocument>> segments, CancellationToken cancellationToken);
    SegmentManifestEntry PublishSegment(IReadOnlyList<ISegmentBuildDocument> documents, ulong segmentId, CancellationToken cancellationToken);
    void PublishManifest(CorpusManifest manifest);
    CorpusManifest ReadManifest();
    MappedSegment OpenSegment(ulong segmentId, bool validateChecksums = true);
    void EnsureSnapshotMetadata();
    StorageReadSnapshot OpenReadSnapshot();
    StorageStats GetStorageStats();
    void ReclaimUnreferencedSegments(CancellationToken cancellationToken);
    // Caller holds the writer lock; this enforces the retained-generation disk bound.
    void EnsureReplacementAllowedUnderWriterLock();
}
