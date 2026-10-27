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
        => index.Build(documents, new BuildOptions(), CancellationToken.None);

    public static Task<BuildStats> Build(
        this ISiftIndex index,
        IDocumentSource documents,
        CancellationToken cancellationToken)
        => index.Build(documents, new BuildOptions(), cancellationToken);

    public static Task<BuildStats> Build(
        this ISiftIndex index,
        IDocumentSource documents,
        BuildOptions options)
        => index.Build(documents, options, CancellationToken.None);

    public static Task<BuildStats> Build(this ISiftIndex index, IAsyncEnumerable<SubmittedDocument> documents)
        => index.Build(documents, new BuildOptions(), CancellationToken.None);

    public static Task<BuildStats> Build(
        this ISiftIndex index,
        IAsyncEnumerable<SubmittedDocument> documents,
        CancellationToken cancellationToken)
        => index.Build(documents, new BuildOptions(), cancellationToken);

    public static Task<BuildStats> Build(
        this ISiftIndex index,
        IAsyncEnumerable<SubmittedDocument> documents,
        BuildOptions options)
        => index.Build(documents, options, CancellationToken.None);

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

    public static CandidateVisitStats VisitCandidates(this IIndexSnapshot snapshot,
        SearchQuery query, PathFilter pathFilter, IDocumentCandidateCollector collector) =>
        snapshot.VisitCandidates(query, pathFilter, collector, new CandidateVisitOptions(), CancellationToken.None);

    public static CandidateVisitStats VisitCandidates(this IIndexSnapshot snapshot,
        SearchQuery query, PathFilter pathFilter, IDocumentCandidateCollector collector, CancellationToken cancellationToken) =>
        snapshot.VisitCandidates(query, pathFilter, collector, new CandidateVisitOptions(), cancellationToken);

    public static CandidateVisitStats VisitCandidates(this IIndexSnapshot snapshot,
        SearchQuery query, PathFilter pathFilter, IDocumentCandidateCollector collector, CandidateVisitOptions options) =>
        snapshot.VisitCandidates(query, pathFilter, collector, options, CancellationToken.None);

    public static Task<OverlayUpdateStats> Update(
        this IOverlaySnapshot snapshot,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions)
    {
        return snapshot.Update(upserts, deletions, CancellationToken.None);
    }

    public static Task<OverlayUpdateStats> Update(
        this IOverlaySnapshot snapshot,
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayUpdateOptions options)
    {
        return snapshot.Update(upserts, deletions, options, CancellationToken.None);
    }

    public static Task<UpdateStats> CommitToBase(this IOverlaySnapshot snapshot, ISiftIndex baseIndex)
        => snapshot.CommitToBase(baseIndex, new UpdateOptions(), CancellationToken.None);

    public static Task<UpdateStats> CommitToBase(
        this IOverlaySnapshot snapshot,
        ISiftIndex baseIndex,
        CancellationToken cancellationToken)
        => snapshot.CommitToBase(baseIndex, new UpdateOptions(), cancellationToken);

    public static Task<UpdateStats> CommitToBase(
        this IOverlaySnapshot snapshot,
        ISiftIndex baseIndex,
        UpdateOptions options)
        => snapshot.CommitToBase(baseIndex, options, CancellationToken.None);

    public static Task<UpdateStats> ApplyDeltaTo(this IOverlaySnapshot snapshot, ISiftIndex target)
        => snapshot.ApplyDeltaTo(target, new UpdateOptions(), CancellationToken.None);

    public static Task<UpdateStats> ApplyDeltaTo(
        this IOverlaySnapshot snapshot,
        ISiftIndex target,
        CancellationToken cancellationToken)
        => snapshot.ApplyDeltaTo(target, new UpdateOptions(), cancellationToken);

    public static Task<UpdateStats> ApplyDeltaTo(
        this IOverlaySnapshot snapshot,
        ISiftIndex target,
        UpdateOptions options)
        => snapshot.ApplyDeltaTo(target, options, CancellationToken.None);
}
