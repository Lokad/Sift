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
        in PathFilter pathFilter,
        IHitCollector collector)
    {
        return snapshot.Search(query, pathFilter, collector, CancellationToken.None);
    }

    public static Task<UpdateStats> Apply(
        this IOverlaySnapshot snapshot,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions)
    {
        return snapshot.Apply(upserts, deletions, CancellationToken.None);
    }

    public static Task<UpdateStats> Apply(
        this IOverlaySnapshot snapshot,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayApplyOptions options)
    {
        return snapshot.Apply(upserts, deletions, options, CancellationToken.None);
    }

    public static Task<UpdateStats> ApplyTo(this IOverlaySnapshot snapshot, ISift target)
        => ApplyTo(snapshot, target, new UpdateOptions(), CancellationToken.None);

    public static Task<UpdateStats> ApplyTo(
        this IOverlaySnapshot snapshot,
        ISift target,
        CancellationToken cancellationToken)
        => ApplyTo(snapshot, target, new UpdateOptions(), cancellationToken);

    public static Task<UpdateStats> ApplyTo(
        this IOverlaySnapshot snapshot,
        ISift target,
        UpdateOptions options)
        => ApplyTo(snapshot, target, options, CancellationToken.None);

    private static Task<UpdateStats> ApplyTo(
        IOverlaySnapshot snapshot,
        ISift target,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        return snapshot.ApplyTo(target, options, cancellationToken);
    }
}
