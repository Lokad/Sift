namespace Lokad.Sift;

public static class SiftContractExtensions
{
    public static IIndexSnapshot OpenSnapshot(this ISift sift)
    {
        return sift.OpenSnapshot(new SearchOptions());
    }

    public static IOverlaySnapshot OpenOverlaySnapshot(this ISift sift)
    {
        return sift.OpenOverlaySnapshot(new SearchOptions());
    }

    public static Task<IndexValidationStats> Validate(this ISift sift)
    {
        return sift.Validate(CancellationToken.None);
    }

    public static Task<BuildStats> Build(this ISift sift, IDocumentSource documents)
        => Build(sift, documents, new BuildOptions(), CancellationToken.None);

    public static Task<BuildStats> Build(
        this ISift sift,
        IDocumentSource documents,
        CancellationToken cancellationToken)
        => Build(sift, documents, new BuildOptions(), cancellationToken);

    public static Task<BuildStats> Build(
        this ISift sift,
        IDocumentSource documents,
        BuildOptions options)
        => Build(sift, documents, options, CancellationToken.None);

    private static Task<BuildStats> Build(
        ISift sift,
        IDocumentSource documents,
        BuildOptions options,
        CancellationToken cancellationToken)
    {
        return sift.Build(documents, options, cancellationToken);
    }

    public static Task<BuildStats> Build(this ISift sift, IAsyncEnumerable<SubmittedDocument> documents)
        => Build(sift, documents, new BuildOptions(), CancellationToken.None);

    public static Task<BuildStats> Build(
        this ISift sift,
        IAsyncEnumerable<SubmittedDocument> documents,
        CancellationToken cancellationToken)
        => Build(sift, documents, new BuildOptions(), cancellationToken);

    public static Task<BuildStats> Build(
        this ISift sift,
        IAsyncEnumerable<SubmittedDocument> documents,
        BuildOptions options)
        => Build(sift, documents, options, CancellationToken.None);

    private static Task<BuildStats> Build(
        ISift sift,
        IAsyncEnumerable<SubmittedDocument> documents,
        BuildOptions options,
        CancellationToken cancellationToken)
    {
        return sift.Build(documents, options, cancellationToken);
    }

    public static Task<UpdateStats> Update(
        this ISift sift,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions)
    {
        return sift.Update(upserts, deletions, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this ISift sift,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options)
    {
        return sift.Update(upserts, deletions, options, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this ISift sift,
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions)
    {
        return sift.Update(upserts, deletions, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this ISift sift,
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options)
    {
        return sift.Update(upserts, deletions, options, CancellationToken.None);
    }

    public static Task<CompactStats> Compact(this ISift sift, CompactOptions options)
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

    public static Task<UpdateStats> CommitToBase(this IOverlaySnapshot snapshot, ISift baseIndex)
        => CommitToBase(snapshot, baseIndex, new UpdateOptions(), CancellationToken.None);

    public static Task<UpdateStats> CommitToBase(
        this IOverlaySnapshot snapshot,
        ISift baseIndex,
        CancellationToken cancellationToken)
        => CommitToBase(snapshot, baseIndex, new UpdateOptions(), cancellationToken);

    public static Task<UpdateStats> CommitToBase(
        this IOverlaySnapshot snapshot,
        ISift baseIndex,
        UpdateOptions options)
        => CommitToBase(snapshot, baseIndex, options, CancellationToken.None);

    private static Task<UpdateStats> CommitToBase(
        IOverlaySnapshot snapshot,
        ISift baseIndex,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        return snapshot.CommitToBase(baseIndex, options, cancellationToken);
    }

    public static Task<UpdateStats> ApplyDeltaTo(this IOverlaySnapshot snapshot, ISift target)
        => ApplyDeltaTo(snapshot, target, new UpdateOptions(), CancellationToken.None);

    public static Task<UpdateStats> ApplyDeltaTo(
        this IOverlaySnapshot snapshot,
        ISift target,
        CancellationToken cancellationToken)
        => ApplyDeltaTo(snapshot, target, new UpdateOptions(), cancellationToken);

    public static Task<UpdateStats> ApplyDeltaTo(
        this IOverlaySnapshot snapshot,
        ISift target,
        UpdateOptions options)
        => ApplyDeltaTo(snapshot, target, options, CancellationToken.None);

    private static Task<UpdateStats> ApplyDeltaTo(
        IOverlaySnapshot snapshot,
        ISift target,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        return snapshot.ApplyDeltaTo(target, options, cancellationToken);
    }
}
