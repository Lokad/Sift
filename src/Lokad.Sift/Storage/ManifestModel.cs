namespace Lokad.Sift.Storage;

public readonly record struct SegmentManifestEntry(
    ulong SegmentId,
    int DocumentCount,
    long ContentBytes,
    uint[] DeadOrdinals,
    long DeadContentBytes)
{
    public int LiveDocumentCount => DocumentCount - DeadOrdinals.Length;
}

public sealed record CorpusManifest(
    Guid CorpusId,
    ulong ContentRevision,
    ulong Generation,
    byte[] CommitMetadata,
    IReadOnlyList<SegmentManifestEntry> Segments)
{
    public CorpusManifest(
        Guid corpusId,
        ulong generation,
        IReadOnlyList<SegmentManifestEntry> segments)
        : this(corpusId, generation, generation, [], segments)
    {
    }
}
