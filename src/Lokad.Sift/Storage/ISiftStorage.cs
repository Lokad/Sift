namespace Lokad.Sift.Storage;

public interface ISiftStorage
{
    bool HasManifest { get; }
    bool IsReclamationPending { get; }
    IDisposable AcquireWriterLock(CancellationToken cancellationToken = default);
    SegmentManifestEntry PublishSegment(IReadOnlyList<ISegmentBuildDocument> documents, ulong segmentId, CancellationToken cancellationToken);
    void PublishManifest(CorpusManifest manifest);
    CorpusManifest ReadManifest();
    MappedSegment OpenSegment(ulong segmentId);
    long GetSegmentBytes(ulong segmentId);
    void EnsureSnapshotMetadata();
    StorageReadSnapshot OpenReadSnapshot();
    StorageStats GetStorageStats();
    void ReclaimUnreferencedSegments(CancellationToken cancellationToken);
    // Caller holds the writer lock; this enforces the retained-generation disk bound.
    void EnsureReplacementAllowedUnderWriterLock();
}
