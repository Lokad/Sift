namespace Lokad.Sift.Storage;

internal readonly record struct SegmentManifestEntry(
    SegmentId SegmentId,
    int DocumentCount,
    long ContentBytes,
    uint[] DeadOrdinals,
    long DeadContentBytes)
{
    public int LiveDocumentCount => DocumentCount - DeadOrdinals.Length;
}

internal sealed record CorpusManifest(
    CorpusId CorpusId,
    ContentRevision ContentRevision,
    StorageGeneration Generation,
    byte[] CommitMetadata,
    IReadOnlyList<SegmentManifestEntry> Segments)
{
    public CorpusManifest(
        CorpusId corpusId,
        StorageGeneration generation,
        IReadOnlyList<SegmentManifestEntry> segments)
        : this(corpusId, new ContentRevision(generation.Value), generation, [], segments)
    {
    }

    public CorpusManifest CopyForPublication()
    {
        var segments = new SegmentManifestEntry[Segments.Count];
        for (var i = 0; i < Segments.Count; i++)
        {
            var segment = Segments[i];
            segments[i] = segment with { DeadOrdinals = [.. segment.DeadOrdinals] };
        }

        return new CorpusManifest(
            CorpusId,
            ContentRevision,
            Generation,
            [.. CommitMetadata],
            segments);
    }
}
