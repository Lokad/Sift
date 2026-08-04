namespace Lokad.Sift;

public static class SiftIndexExtensions
{
    public static IIndexSnapshot OpenSnapshot(this ISiftIndex sift)
    {
        return sift.OpenSnapshot(new SearchOptions());
    }

    public static IOverlaySnapshot OpenOverlaySnapshot(this ISiftIndex sift)
    {
        return sift.OpenOverlaySnapshot(new SearchOptions());
    }

    public static Task<IndexValidationStats> Validate(this ISiftIndex sift)
    {
        return sift.Validate(CancellationToken.None);
    }

    public static Task<BuildStats> Build(this ISiftIndex sift, IDocumentSource documents)
        => Build(sift, documents, new BuildOptions(), CancellationToken.None);

    public static Task<BuildStats> Build(
        this ISiftIndex sift,
        IDocumentSource documents,
        CancellationToken cancellationToken)
        => Build(sift, documents, new BuildOptions(), cancellationToken);

    public static Task<BuildStats> Build(
        this ISiftIndex sift,
        IDocumentSource documents,
        BuildOptions options)
        => Build(sift, documents, options, CancellationToken.None);

    private static Task<BuildStats> Build(
        ISiftIndex sift,
        IDocumentSource documents,
        BuildOptions options,
        CancellationToken cancellationToken)
    {
        return sift.Build(documents, options, cancellationToken);
    }

    public static Task<BuildStats> Build(this ISiftIndex sift, IAsyncEnumerable<SubmittedDocument> documents)
        => Build(sift, documents, new BuildOptions(), CancellationToken.None);

    public static Task<BuildStats> Build(
        this ISiftIndex sift,
        IAsyncEnumerable<SubmittedDocument> documents,
        CancellationToken cancellationToken)
        => Build(sift, documents, new BuildOptions(), cancellationToken);

    public static Task<BuildStats> Build(
        this ISiftIndex sift,
        IAsyncEnumerable<SubmittedDocument> documents,
        BuildOptions options)
        => Build(sift, documents, options, CancellationToken.None);

    private static Task<BuildStats> Build(
        ISiftIndex sift,
        IAsyncEnumerable<SubmittedDocument> documents,
        BuildOptions options,
        CancellationToken cancellationToken)
    {
        return sift.Build(documents, options, cancellationToken);
    }

    public static Task<UpdateStats> Update(
        this ISiftIndex sift,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions)
    {
        return sift.Update(upserts, deletions, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this ISiftIndex sift,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options)
    {
        return sift.Update(upserts, deletions, options, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this ISiftIndex sift,
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions)
    {
        return sift.Update(upserts, deletions, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this ISiftIndex sift,
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options)
    {
        return sift.Update(upserts, deletions, options, CancellationToken.None);
    }

    public static Task<CompactStats> Compact(this ISiftIndex sift, CompactOptions options)
    {
        return sift.Compact(options, CancellationToken.None);
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
