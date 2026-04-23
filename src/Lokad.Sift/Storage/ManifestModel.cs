namespace Lokad.Sift.Storage;

public readonly record struct SegmentManifestEntry(
    ulong SegmentId,
    int DocumentCount,
    long ContentBytes,
    uint[] DeadOrdinals)
{
    public int LiveDocumentCount => DocumentCount - DeadOrdinals.Length;
}

public sealed record CorpusManifest(
    Guid CorpusId,
    ulong Generation,
    IReadOnlyList<SegmentManifestEntry> Segments);
