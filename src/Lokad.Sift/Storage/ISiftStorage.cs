namespace Lokad.Sift.Storage;

internal interface ISiftStorage
{
    bool HasManifest { get; }
    bool IsReclamationPending { get; }
    IStorageWriteSession AcquireWriteSession(CancellationToken cancellationToken);
    CorpusManifest ReadManifest();
    CorpusManifest RefreshManifest();
    MappedSegment OpenSegment(SegmentId segmentId);
    MappedSegment OpenSegment(SegmentId segmentId, CancellationToken cancellationToken);
    void ValidateSegment(SegmentId segmentId, CancellationToken cancellationToken);
    long GetSegmentBytes(SegmentId segmentId);
    long? GetAvailableBytes();
    void EnsureSnapshotMetadata();
    StorageReadSnapshot OpenReadSnapshot();
    StorageReadSnapshot OpenReadSnapshotForValidation();
    StorageStats GetStorageStats();
    void ReclaimUnreferencedSegments(CancellationToken cancellationToken);
}

internal interface IStorageWriteSession : IDisposable
{
    CorpusManifest? CurrentManifest { get; }
    SegmentManifestEntry PublishSegment<TDocument>(
        IReadOnlyList<TDocument> documents,
        SegmentId segmentId,
        long minimumFreeBytesAfterPublication,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument;
    void PublishManifest(CorpusManifest manifest);
    bool CanReplaceSegments();
    void EnsureReplacementAllowed();
    void PrepareFullBuild(bool discardCorruptState);
    void CleanupFailedMutation(IReadOnlyCollection<SegmentId> attemptedSegmentIds);
}
