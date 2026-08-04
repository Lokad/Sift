namespace Lokad.Sift.Storage;

internal interface ISiftStorage
{
    bool HasManifest { get; }
    bool IsReclamationPending { get; }
    IStorageWriteSession AcquireWriteSession(CancellationToken cancellationToken);
    CorpusManifest ReadManifest();
    MappedSegment OpenSegment(ulong segmentId);
    MappedSegment OpenSegment(ulong segmentId, CancellationToken cancellationToken);
    void ValidateSegment(ulong segmentId, CancellationToken cancellationToken);
    long GetSegmentBytes(ulong segmentId);
    long? GetAvailableBytes();
    void EnsureSnapshotMetadata();
    StorageReadSnapshot OpenReadSnapshot();
    StorageReadSnapshot OpenReadSnapshotForValidation();
    StorageStats GetStorageStats();
    void ReclaimUnreferencedSegments(CancellationToken cancellationToken);
}

internal interface IStorageWriteSession : IDisposable
{
    SegmentManifestEntry PublishSegment<TDocument>(
        IReadOnlyList<TDocument> documents,
        ulong segmentId,
        long minimumFreeBytesAfterPublication,
        CancellationToken cancellationToken)
        where TDocument : ISegmentBuildDocument;
    void PublishManifest(CorpusManifest manifest);
    bool CanReplaceSegments();
    void EnsureReplacementAllowed();
    void PrepareFullBuild(bool discardCorruptState);
    void CleanupFailedMutation(IReadOnlyCollection<ulong> attemptedSegmentIds);
}
