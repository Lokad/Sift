namespace Lokad.Sift;

public static class SiftContractExtensions
{
    public static IIndexSnapshot OpenSnapshot(this ISift sift)
    {
        ArgumentNullException.ThrowIfNull(sift);
        return sift.OpenSnapshot(new SearchOptions());
    }

    public static IOverlaySnapshot OpenOverlaySnapshot(this ISift sift)
    {
        ArgumentNullException.ThrowIfNull(sift);
        return sift.OpenOverlaySnapshot(new SearchOptions());
    }

    public static Task<IndexValidationStats> Validate(this ISift sift)
    {
        ArgumentNullException.ThrowIfNull(sift);
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
        ArgumentNullException.ThrowIfNull(sift);
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
        ArgumentNullException.ThrowIfNull(sift);
        return sift.Build(documents, options, cancellationToken);
    }

    public static Task<UpdateStats> Update(
        this ISift sift,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions)
    {
        ArgumentNullException.ThrowIfNull(sift);
        return sift.Update(upserts, deletions, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this ISift sift,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(sift);
        return sift.Update(upserts, deletions, options, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this ISift sift,
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions)
    {
        ArgumentNullException.ThrowIfNull(sift);
        return sift.Update(upserts, deletions, CancellationToken.None);
    }

    public static Task<UpdateStats> Update(
        this ISift sift,
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(sift);
        return sift.Update(upserts, deletions, options, CancellationToken.None);
    }

    public static Task<CompactStats> Compact(this ISift sift, CompactOptions options)
    {
        ArgumentNullException.ThrowIfNull(sift);
        return sift.Compact(options, CancellationToken.None);
    }

    public static DocumentVisitStats VisitDocuments(
        this IIndexSnapshot snapshot,
        IDocumentKeyCollector collector)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.VisitDocuments(collector, CancellationToken.None);
    }

    public static SearchStats Search(
        this IIndexSnapshot snapshot,
        in SearchQuery query,
        in PathFilter pathFilter,
        IHitCollector collector)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Search(query, pathFilter, collector, CancellationToken.None);
    }

    public static Task<UpdateStats> Apply(
        this IOverlaySnapshot snapshot,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Apply(upserts, deletions, CancellationToken.None);
    }

    public static Task<UpdateStats> Apply(
        this IOverlaySnapshot snapshot,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayApplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
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
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ApplyTo(target, options, cancellationToken);
    }
}
