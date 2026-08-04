namespace Lokad.Sift;

public static class SiftIndexExtensions
{
    public static IIndexSnapshot OpenSnapshot(this ISiftIndex index)
    {
        return index.OpenSnapshot(new SearchOptions());
    }

    public static IOverlaySnapshot OpenOverlaySnapshot(this ISiftIndex index)
    {
        return index.OpenOverlaySnapshot(new SearchOptions());
    }

    public static Task<IndexValidationStats> Validate(this ISiftIndex index)
    {
        return index.Validate(CancellationToken.None);
    }

    public static Task<BuildStats> Build(this ISiftIndex index, IDocumentSource documents)
        => Build(index, documents, new BuildOptions(), CancellationToken.None);

    public static Task<BuildStats> Build(
        this ISiftIndex index,
        IDocumentSource documents,
        CancellationToken cancellationToken)
        => Build(index, documents, new BuildOptions(), cancellationToken);

    public static Task<BuildStats> Build(
        this ISiftIndex index,
        IDocumentSource documents,
        BuildOptions options)
        => Build(index, documents, options, CancellationToken.None);

    private static Task<BuildStats> Build(
        ISiftIndex index,
        IDocumentSource documents,
        BuildOptions options,
        CancellationToken cancellationToken)
    {
        return index.Build(documents, options, cancellationToken);
    }

    public static Task<BuildStats> Build(this ISiftIndex index, IAsyncEnumerable<SubmittedDocument> documents)
        => Build(index, documents, new BuildOptions(), CancellationToken.None);

    public static Task<BuildStats> Build(
        this ISiftIndex index,
        IAsyncEnumerable<SubmittedDocument> documents,
        CancellationToken cancellationToken)
        => Build(index, documents, new BuildOptions(), cancellationToken);

    public static Task<BuildStats> Build(
        this ISiftIndex index,
        IAsyncEnumerable<SubmittedDocument> documents,
        BuildOptions options)
        => Build(index, documents, options, CancellationToken.None);

    private static Task<BuildStats> Build(
        ISiftIndex index,
        IAsyncEnumerable<SubmittedDocument> documents,
        BuildOptions options,
        CancellationToken cancellationToken)
    {
        return index.Build(documents, options, cancellationToken);
    }

    public static Task<UpdateStats> Update(
        this ISiftIndex index,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions)
    {
        return index.Update(upserts, deletions, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this ISiftIndex index,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options)
    {
        return index.Update(upserts, deletions, options, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this ISiftIndex index,
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions)
    {
        return index.Update(upserts, deletions, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this ISiftIndex index,
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options)
    {
        return index.Update(upserts, deletions, options, CancellationToken.None);
    }

    public static Task<CompactStats> Compact(this ISiftIndex index, CompactOptions options)
    {
        return index.Compact(options, CancellationToken.None);
    }

    public static DocumentVisitStats VisitDocuments(
        this IIndexSnapshot snapshot,
        IDocumentKeyCollector collector)
    {
        return snapshot.VisitDocuments(collector, CancellationToken.None);
    }

    public static SearchStats Search(
        this IIndexSnapshot snapshot,
        SearchQuery query,
        PathFilter pathFilter,
        IHitCollector collector)
    {
        return snapshot.Search(query, pathFilter, collector, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this IOverlaySnapshot snapshot,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions)
    {
        return snapshot.Update(upserts, deletions, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this IOverlaySnapshot snapshot,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayUpdateOptions options)
    {
        return snapshot.Update(upserts, deletions, options, CancellationToken.None);
    }

    public static Task<UpdateStats> CommitToBase(this IOverlaySnapshot snapshot, ISiftIndex baseIndex)
        => CommitToBase(snapshot, baseIndex, new UpdateOptions(), CancellationToken.None);

    public static Task<UpdateStats> CommitToBase(
        this IOverlaySnapshot snapshot,
        ISiftIndex baseIndex,
        CancellationToken cancellationToken)
        => CommitToBase(snapshot, baseIndex, new UpdateOptions(), cancellationToken);

    public static Task<UpdateStats> CommitToBase(
        this IOverlaySnapshot snapshot,
        ISiftIndex baseIndex,
        UpdateOptions options)
        => CommitToBase(snapshot, baseIndex, options, CancellationToken.None);

    private static Task<UpdateStats> CommitToBase(
        IOverlaySnapshot snapshot,
        ISiftIndex baseIndex,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        return snapshot.CommitToBase(baseIndex, options, cancellationToken);
    }

    public static Task<UpdateStats> ApplyDeltaTo(this IOverlaySnapshot snapshot, ISiftIndex target)
        => ApplyDeltaTo(snapshot, target, new UpdateOptions(), CancellationToken.None);

    public static Task<UpdateStats> ApplyDeltaTo(
        this IOverlaySnapshot snapshot,
        ISiftIndex target,
        CancellationToken cancellationToken)
        => ApplyDeltaTo(snapshot, target, new UpdateOptions(), cancellationToken);

    public static Task<UpdateStats> ApplyDeltaTo(
        this IOverlaySnapshot snapshot,
        ISiftIndex target,
        UpdateOptions options)
        => ApplyDeltaTo(snapshot, target, options, CancellationToken.None);

    private static Task<UpdateStats> ApplyDeltaTo(
        IOverlaySnapshot snapshot,
        ISiftIndex target,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        return snapshot.ApplyDeltaTo(target, options, cancellationToken);
    }
}
