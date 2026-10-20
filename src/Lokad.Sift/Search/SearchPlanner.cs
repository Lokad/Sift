using Lokad.Sift;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal static class SearchPlanner
{
    internal static int GetTargetCandidateDocuments(int scopeDocuments, SiftOptions options)
    {
        var target = int.MaxValue;
        if (options.DirectScanScopeDocumentsThreshold > 0)
        {
            target = Math.Min(target, options.DirectScanScopeDocumentsThreshold);
        }

        if (options.DirectScanCandidateDensityThreshold > 0)
        {
            target = Math.Min(target, (int)Math.Ceiling(scopeDocuments * options.DirectScanCandidateDensityThreshold));
        }

        return target == int.MaxValue ? target : Math.Max(target, 1);
    }

    // Independence is a scoring heuristic, not a cardinality bound: n-grams can
    // be strongly correlated. Exact candidate generation determines the work.
    internal static double EstimateCandidateDocuments(ReadOnlySpan<SelectedGram> selected, int scopeDocuments)
    {
        if (scopeDocuments <= 0 || selected.Length == 0)
        {
            return 0;
        }

        var estimate = (double)scopeDocuments;
        for (var i = 0; i < selected.Length; i++)
        {
            var ratio = Math.Clamp((double)selected[i].DocFreq / scopeDocuments, 0d, 1d);
            estimate *= ratio;
        }

        return estimate;
    }

    internal static bool ShouldPreferLiteralDirectScan(
        int scopeDocuments,
        long scopeBytes,
        int patternBytes,
        ReadOnlySpan<SelectedGram> selected,
        double estimatedCandidates,
        SiftOptions options)
    {
        if (selected.Length == 0)
        {
            return true;
        }

        if (scopeDocuments <= options.DirectScanScopeDocumentsThreshold && scopeBytes <= options.DirectScanScopeBytesThreshold)
        {
            return true;
        }

        if (options.DirectScanCandidateDensityThreshold > 0 &&
            estimatedCandidates >= Math.Ceiling(scopeDocuments * options.DirectScanCandidateDensityThreshold))
        {
            return true;
        }

        var directScanCost = (double)Math.Max(scopeBytes, scopeDocuments);
        var indexedCost = EstimateIndexedCost(patternBytes, estimatedCandidates, selected, RegexExecutionPlan.Literal);
        return indexedCost >= directScanCost;
    }

    internal static bool ShouldPreferRegexDirectScan(
        SegmentScope scope,
        SelectedGram[] selected,
        double estimatedCandidates,
        double directScanCost,
        double indexedCost,
        bool includesUnicodeFallback,
        SiftOptions options)
    {
        if (selected.Length == 0)
        {
            return true;
        }

        if (scope.Count <= options.DirectScanScopeDocumentsThreshold && scope.Bytes <= options.DirectScanScopeBytesThreshold)
        {
            return true;
        }

        if (options.DirectScanCandidateDensityThreshold > 0 &&
            estimatedCandidates >= Math.Ceiling(scope.Count * options.DirectScanCandidateDensityThreshold))
        {
            return true;
        }

        if (includesUnicodeFallback &&
            options.DirectScanCandidateDensityThreshold > 0 &&
            scope.NonAsciiBytes >= Math.Ceiling(scope.Bytes * options.DirectScanCandidateDensityThreshold))
        {
            return true;
        }

        return indexedCost >= directScanCost;
    }

    internal static double EstimateDirectScanCost(SegmentScope scope, in RegexExecutionPlan regexPlan)
    {
        return Math.Max(scope.Bytes, scope.Count) * regexPlan.DirectScanCostMultiplier;
    }

    internal static double EstimateIndexedCost(int patternBytes, double estimatedCandidates, ReadOnlySpan<SelectedGram> selected, in RegexExecutionPlan regexPlan)
    {
        long postingsBytes = 0;
        for (var i = 0; i < selected.Length; i++)
        {
            postingsBytes += selected[i].PostingsBytes;
        }

        var verifierCost = estimatedCandidates * Math.Max(patternBytes, selected.Length == 0 ? 0 : selected[0].GramLength * 4) * regexPlan.VerifierCostMultiplier;
        return postingsBytes + verifierCost;
    }

    public static SegmentSearchPlan CreateExecutionPlan(
        SegmentCache cache,
        PathFilterMatcher matcher,
        SearchQuery query,
        PreparedSearchQuery preparedQuery,
        SiftOptions options,
        CancellationToken cancellationToken)
    {
        return CreateSegmentPlan(
            cache,
            matcher,
            query,
            preparedQuery,
            options,
            retainAlternatives: false,
            cancellationToken);
    }

    public static SegmentSearchPlan CreateDiagnosticPlan(
        SegmentCache cache,
        PathFilterMatcher matcher,
        SearchQuery query,
        PreparedSearchQuery preparedQuery,
        SiftOptions options,
        CancellationToken cancellationToken)
    {
        return CreateSegmentPlan(
            cache,
            matcher,
            query,
            preparedQuery,
            options,
            retainAlternatives: true,
            cancellationToken);
    }

    private static SegmentSearchPlan CreateSegmentPlan(
        SegmentCache cache,
        PathFilterMatcher matcher,
        SearchQuery query,
        PreparedSearchQuery preparedQuery,
        SiftOptions options,
        bool retainAlternatives,
        CancellationToken cancellationToken)
    {
        var pathScope = new SegmentScopeBuilder(
            cache,
            matcher,
            options.MaxSelectedNgrams,
            cancellationToken).Build();
        var features = new SearchQueryFeatures(query, preparedQuery);
        var candidates = SearchPlanCandidateBuilder.BuildCandidates(
            cache.Segment,
            pathScope.Scope,
            in features,
            options);
        var contentPlan = new SearchPlannerDecision(
            SearchPlanSelector.ChooseBest(candidates),
            retainAlternatives ? [.. candidates] : []);
        return new SegmentSearchPlan(
            pathScope.Scope,
            contentPlan,
            pathScope.EstimatedCost);
    }
}

internal readonly record struct SearchQueryFeatures(
    SearchQuery Query,
    PreparedSearchQuery Prepared)
{
    public CaseMode ResolvedCaseMode => Prepared.ResolvedCaseMode;
    public SearchQueryAnchors Anchors => Prepared.Anchors;
    public RegexExecutionPlan RegexPlan => Prepared.RegexPlan;
    public PreparedLiteralQuery? Literal => Prepared.Literal;
    public PreparedSearchText[] MandatoryTexts => Prepared.MandatoryTexts;
    public PreparedSearchText[] AdvisoryTexts => Prepared.AdvisoryTexts;
    public PreparedSearchText[] LeadingLiterals => Prepared.LeadingLiterals;
    public bool IsLiteralLike => Query.PatternMode == PatternMode.Literal || RegexPlan.IsExactLiteral;
    public bool HasMandatoryFragments => MandatoryTexts.Length > 0;
}

internal readonly record struct SearchPlanCandidate(
    LiteralPlan CandidatePlan,
    SearchCandidateKind CandidateKind,
    SearchCandidateCombination CandidateCombination,
    SearchVerifierKind VerifierKind,
    double EstimatedDirectScanCost,
    double? EstimatedIndexedCost,
    double EstimatedCandidateDocuments,
    double EstimatedCost);

internal readonly record struct SearchPlannerDecision(
    SearchPlanCandidate ChosenPlan,
    SearchPlanCandidate[] Alternatives);

internal readonly record struct SegmentSearchPlan(
    SegmentScope Scope,
    SearchPlannerDecision ContentPlan,
    double EstimatedPathFilterCost);

