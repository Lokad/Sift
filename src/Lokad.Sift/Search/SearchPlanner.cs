using Lokad.Sift;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal static class SearchPlanner
{
    public static SegmentSearchPlan CreateSegmentPlan(
        SegmentCache cache,
        PathFilterMatcher matcher,
        in SearchQuery query,
        CaseMode resolvedCaseMode,
        in SearchQueryAnchors anchors,
        in RegexExecutionPlan regexPlan,
        SiftOptions options,
        CancellationToken cancellationToken)
    {
        var scope = BuildScope(
            cache,
            matcher,
            cancellationToken,
            out var prefixDocuments,
            out var pathIndexCandidates,
            out var pathVerificationKind);
        var contentPlan = CreatePlan(cache.Segment, scope, query, resolvedCaseMode, anchors, regexPlan, options);
        return new SegmentSearchPlan(
            scope,
            contentPlan,
            prefixDocuments,
            pathIndexCandidates,
            pathVerificationKind,
            EstimatePathFilterCost(prefixDocuments, pathIndexCandidates, scope.Count, matcher.UsesPathIndex, pathVerificationKind));
    }

    public static SearchPlannerDecision CreatePlan(MappedSegment segment, SegmentScope scope, in SearchQuery query, CaseMode resolvedCaseMode, in SearchQueryAnchors anchors, in RegexExecutionPlan regexPlan, SiftOptions options)
    {
        var features = BuildFeatures(query, resolvedCaseMode, anchors, regexPlan);
        var candidates = SearchPlanCandidateBuilder.Enumerate(segment, scope, in features, options);
        return FinalizePlan(candidates);
    }

    private static SearchQueryFeatures BuildFeatures(in SearchQuery query, CaseMode resolvedCaseMode, in SearchQueryAnchors anchors, in RegexExecutionPlan regexPlan)
    {
        return new SearchQueryFeatures(
            query,
            resolvedCaseMode,
            anchors,
            regexPlan,
            query.PatternMode == PatternMode.Literal || regexPlan.IsExactLiteral,
            anchors.HasMandatoryAnchors);
    }

    private static SearchPlannerDecision FinalizePlan(List<SearchPlanCandidate> candidates)
    {
        var scored = SearchPlanScorer.Score(candidates);
        var chosen = SearchPlanScorer.ChooseBest(scored);
        return new SearchPlannerDecision(chosen, [.. candidates]);
    }

    internal static SelectedNGram[] ToSelectedNgrams(ReadOnlySpan<SelectedGram> grams)
    {
        if (grams.Length == 0)
        {
            return [];
        }

        var selected = new SelectedNGram[grams.Length];
        for (var i = 0; i < grams.Length; i++)
        {
            selected[i] = new SelectedNGram(grams[i].Text, grams[i].GramLength, grams[i].DocFreq);
        }

        return selected;
    }

    internal static SearchCandidateKind GetCandidateKind(in SearchQuery query, ReadOnlySpan<SelectedNGram> selected, in RegexExecutionPlan regexPlan, bool usesIndex, bool unionCandidates, bool branchUnion)
    {
        var selectedLength = selected.Length == 0 ? 0 : selected[0].Length;
        return SearchPlanKinds.GetCandidateKind(query, selectedLength, regexPlan, usesIndex, unionCandidates, branchUnion);
    }

    internal static SearchPlanFamily GetPlanFamily(in SearchQuery query, ReadOnlySpan<SelectedNGram> selected, bool hasMandatoryFragments, in RegexExecutionPlan regexPlan, bool usesIndex, bool unionCandidates, bool branchUnion)
    {
        var selectedLength = selected.Length == 0 ? 0 : selected[0].Length;
        return SearchPlanKinds.GetPlanFamily(query, selectedLength, hasMandatoryFragments, regexPlan, usesIndex, unionCandidates, branchUnion);
    }

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

    internal static double EstimateCandidateUpperBound(ReadOnlySpan<SelectedGram> selected, int scopeDocuments)
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

    internal static bool ShouldPreferLiteralDirectScan(int scopeDocuments, long scopeBytes, int patternBytes, ReadOnlySpan<SelectedGram> selected, SiftOptions options)
    {
        if (selected.Length == 0)
        {
            return true;
        }

        if (scopeDocuments <= options.DirectScanScopeDocumentsThreshold && scopeBytes <= options.DirectScanScopeBytesThreshold)
        {
            return true;
        }

        var estimatedCandidates = EstimateCandidateUpperBound(selected, scopeDocuments);
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

    private static SegmentScope BuildScope(
        SegmentCache cache,
        PathFilterMatcher matcher,
        CancellationToken cancellationToken,
        out int prefixDocuments,
        out int pathIndexCandidates,
        out PathVerificationKind pathVerificationKind)
    {
        var prefixRange = matcher.PathPrefix is null
            ? (StartInclusive: 0u, EndExclusive: (uint)cache.Segment.DocumentCount)
            : cache.Segment.FindPathPrefixRange(matcher.PathPrefix);
        prefixDocuments = (int)(prefixRange.EndExclusive - prefixRange.StartInclusive);

        OrdinalSet scopedOrdinals;
        if (matcher.UsesPathIndex)
        {
            scopedOrdinals = CandidateGenerator.Generate(
                cache.Segment,
                OrdinalSet.FromRange(prefixRange.StartInclusive, prefixRange.EndExclusive, []),
                new IndexRegionPair(SegmentRegionId.PathTriCsLex, SegmentRegionId.PathTriCsPost),
                matcher.GetPathTrigrams()).Ordinals;
        }
        else
        {
            scopedOrdinals = OrdinalSet.FromRange(prefixRange.StartInclusive, prefixRange.EndExclusive, []);
        }

        pathIndexCandidates = scopedOrdinals.Count;
        pathVerificationKind = matcher.RequiresPathVerification
            ? matcher.RequiresStringPathVerification ? PathVerificationKind.String : PathVerificationKind.Utf8
            : PathVerificationKind.None;

        if (!matcher.RequiresPathVerification && !matcher.UsesPathIndex)
        {
            var ordinals = OrdinalSet.FromRange(
                prefixRange.StartInclusive,
                prefixRange.EndExclusive,
                cache.DeadOrdinals);
            if (prefixRange.StartInclusive == 0 &&
                prefixRange.EndExclusive == (uint)cache.Segment.DocumentCount)
            {
                return new SegmentScope(
                    ordinals,
                    cache.LiveContentBytes,
                    cache.LiveNonAsciiDocuments,
                    cache.LiveNonAsciiBytes);
            }

            var metrics = GetScopeMetrics(cache, ordinals, cancellationToken);
            return new SegmentScope(
                ordinals,
                metrics.ContentBytes,
                metrics.NonAsciiDocuments,
                metrics.NonAsciiBytes);
        }

        var acceptedOrdinals = new List<uint>(scopedOrdinals.Count);
        long bytes = 0;
        var nonAsciiDocuments = 0;
        long nonAsciiBytes = 0;
        var deadIndex = 0;
        var scanned = 0;
        foreach (var ordinal in scopedOrdinals)
        {
            if ((scanned++ & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            while (deadIndex < cache.DeadOrdinals.Length && cache.DeadOrdinals[deadIndex] < ordinal)
            {
                deadIndex++;
            }

            if (deadIndex < cache.DeadOrdinals.Length && cache.DeadOrdinals[deadIndex] == ordinal)
            {
                continue;
            }

            var document = cache.Segment.GetDocument(ordinal);
            if (matcher.RequiresPathVerification)
            {
                var isMatch = matcher.RequiresStringPathVerification
                    ? matcher.IsMatch(cache.Segment.GetPath(document))
                    : matcher.IsMatch(cache.Segment.GetPathBytes(document));
                if (!isMatch)
                {
                    continue;
                }
            }

            acceptedOrdinals.Add(ordinal);
            bytes += document.ContentLength;
            if ((document.Flags & (1 << 1)) == 0)
            {
                nonAsciiDocuments++;
                nonAsciiBytes += document.ContentLength;
            }
        }

        return new SegmentScope(
            OrdinalSet.FromSorted([.. acceptedOrdinals]),
            bytes,
            nonAsciiDocuments,
            nonAsciiBytes);

        static ScopeMetrics GetScopeMetrics(
            SegmentCache cache,
            OrdinalSet ordinals,
            CancellationToken cancellationToken)
        {
            long bytes = 0;
            var nonAsciiDocuments = 0;
            long nonAsciiBytes = 0;
            var scanned = 0;
            foreach (var ordinal in ordinals)
            {
                if ((scanned++ & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var document = cache.Segment.GetDocument(ordinal);
                bytes += document.ContentLength;
                if ((document.Flags & (1 << 1)) == 0)
                {
                    nonAsciiDocuments++;
                    nonAsciiBytes += document.ContentLength;
                }
            }

            return new ScopeMetrics(bytes, nonAsciiDocuments, nonAsciiBytes);
        }
    }

    private static double EstimatePathFilterCost(int prefixDocuments, int pathIndexCandidates, int scopedDocuments, bool usesPathIndex, PathVerificationKind pathVerificationKind)
    {
        double cost = prefixDocuments;
        if (usesPathIndex)
        {
            cost += pathIndexCandidates * 0.5d;
        }

        cost += pathVerificationKind switch
        {
            PathVerificationKind.Utf8 => scopedDocuments,
            PathVerificationKind.String => scopedDocuments * 4d,
            _ => 0d
        };

        return cost;
    }
}

internal readonly record struct ScopeMetrics(
    long ContentBytes,
    int NonAsciiDocuments,
    long NonAsciiBytes);

internal readonly record struct SearchQueryFeatures(
    SearchQuery Query,
    CaseMode ResolvedCaseMode,
    SearchQueryAnchors Anchors,
    RegexExecutionPlan RegexPlan,
    bool IsLiteralLike,
    bool HasMandatoryFragments);

internal readonly record struct SearchPlanCandidate(
    LiteralPlan CandidatePlan,
    SearchPlanFamily PlanFamily,
    SearchCandidateKind CandidateKind,
    SearchVerifierKind VerifierKind,
    double EstimatedDirectScanCost,
    double? EstimatedIndexedCost,
    double EstimatedCandidateDocuments,
    SelectedNGram[] SelectedNgrams,
    double EstimatedCost);

internal readonly record struct SearchPlannerDecision(
    SearchPlanCandidate ChosenPlan,
    SearchPlanCandidate[] Alternatives);

internal readonly record struct SegmentSearchPlan(
    SegmentScope Scope,
    SearchPlannerDecision ContentPlan,
    int PrefixDocuments,
    int PathIndexCandidates,
    PathVerificationKind PathVerificationKind,
    double EstimatedPathFilterCost);

internal enum PathVerificationKind
{
    None,
    Utf8,
    String
}

