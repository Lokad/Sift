using Lokad.Sift;

// This assembly is compiled against the immutable published 0.3.0 package, then
// loaded by consumers of the current package to check default-interface dispatch.
public sealed class PublishedSnapshot(IIndexSnapshot inner) : IIndexSnapshot
{
    public IndexIdentity Identity => inner.Identity;
    public ReadOnlyMemory<byte> CommitMetadata => inner.CommitMetadata;
    public DocumentVisitStats VisitDocuments(IDocumentKeyCollector collector, CancellationToken token) => inner.VisitDocuments(collector, token);
    public SearchPlan Explain(SearchQuery query, PathFilter filter) => inner.Explain(query, filter);
    public SearchStats Search(SearchQuery query, PathFilter filter, IHitCollector collector, CancellationToken token) => inner.Search(query, filter, collector, token);
    public MaterializedHit Materialize(in RawHit hit, int before, int after) => inner.Materialize(hit, before, after);
    public MaterializedHitWithLine MaterializeWithMatchingLine(in RawHit hit, int before, int after) => inner.MaterializeWithMatchingLine(hit, before, after);
    public bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content) => inner.TryGetDocumentBytes(key, out content);
    public void Dispose() { }
}
