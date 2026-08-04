namespace Lokad.Sift.Storage;

// Holding this object open pins every pack named by Manifest across processes.
internal sealed class StorageReadSnapshot : IDisposable
{
    private IDisposable? _lease;

    internal StorageReadSnapshot(CorpusManifest manifest, IDisposable lease)
    {
        Manifest = manifest;
        _lease = lease;
    }

    public CorpusManifest Manifest { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}

internal readonly record struct SegmentReclamationStats(
    int ReclaimedSegmentCount,
    long ReclaimedSegmentBytes,
    int DeferredSegmentCount,
    long DeferredSegmentBytes,
    int FailedSegmentCount,
    long FailedSegmentBytes,
    int BlockedSegmentCount,
    long BlockedSegmentBytes,
    bool IsBlocked,
    int LeasedGenerationCount,
    ulong? OldestLeasedGeneration);
