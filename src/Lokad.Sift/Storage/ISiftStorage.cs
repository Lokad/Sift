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
    void ValidateSegment(SegmentManifestEntry entry, CancellationToken cancellationToken);
    long GetSegmentBytes(SegmentId segmentId);
    long? GetAvailableBytes();
    void EnsureSnapshotMetadata();
    StorageReadSnapshot OpenReadSnapshot();
    StorageReadSnapshot OpenReadSnapshotForValidation();
    StorageStats GetStorageStats();
    void ReclaimUnreferencedSegments(CancellationToken cancellationToken);
}

internal static class SiftStorageExtensions
{
    public static MappedSegment OpenSegment(
        this ISiftStorage storage,
        SegmentManifestEntry entry,
        CancellationToken cancellationToken)
    {
        var segment = storage.OpenSegment(entry.SegmentId, cancellationToken);
        try
        {
            segment.ValidateManifestEntry(entry);
            return segment;
        }
        catch
        {
            segment.Dispose();
            throw;
        }
    }
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
