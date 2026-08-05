using System.Runtime.InteropServices;

namespace Lokad.Sift.Search;

internal static class SearchPlanCombiner
{
    public static SearchPlan Combine(SearchPlan basePlan, SearchPlan overlayPlan)
    {
        var segmentCount = basePlan.SegmentCount + overlayPlan.SegmentCount;
        var candidateKind = MergeStrategy(
            basePlan.CandidateKind,
            basePlan.SegmentCount,
            overlayPlan.CandidateKind,
            overlayPlan.SegmentCount,
            SearchCandidateKind.Mixed);
        var candidateCombination = MergeStrategy(
            basePlan.CandidateCombination,
            basePlan.SegmentCount,
            overlayPlan.CandidateCombination,
            overlayPlan.SegmentCount,
            SearchCandidateCombination.Mixed);
        var verifierKind = MergeStrategy(
            basePlan.VerifierKind,
            basePlan.SegmentCount,
            overlayPlan.VerifierKind,
            overlayPlan.SegmentCount,
            SearchVerifierKind.Mixed);
        var estimatedCost = basePlan.EstimatedCost + overlayPlan.EstimatedCost;
        var plannerAlternatives = MergePlannerAlternatives(
            basePlan.PlannerAlternatives,
            overlayPlan.PlannerAlternatives);
        var runnerUp = plannerAlternatives.FirstOrDefault(alternative =>
            alternative.SegmentCount == segmentCount &&
            (alternative.CandidateKind != candidateKind ||
             alternative.CandidateCombination != candidateCombination ||
             alternative.VerifierKind != verifierKind));

        return new SearchPlan(
            patternKind: basePlan.PatternKind,
            resolvedCaseMode: basePlan.ResolvedCaseMode,
            scopeDocumentCount: basePlan.ScopeDocumentCount + overlayPlan.ScopeDocumentCount,
            scopeByteCount: basePlan.ScopeByteCount + overlayPlan.ScopeByteCount,
            segmentCount: segmentCount,
            mandatoryFragments: MergeStrings(basePlan.MandatoryFragments, overlayPlan.MandatoryFragments),
            advisorySeeds: MergeStrings(basePlan.AdvisorySeeds, overlayPlan.AdvisorySeeds),
            selectedNgrams: MergeSelectedNgrams(basePlan.SelectedNgrams, overlayPlan.SelectedNgrams),
            usesPathPrefixFilter: basePlan.UsesPathPrefixFilter,
            usesPathRegexFilter: basePlan.UsesPathRegexFilter,
            usesIncludeGlobs: basePlan.UsesIncludeGlobs,
            usesExcludeGlobs: basePlan.UsesExcludeGlobs,
            usesPathIndex: basePlan.UsesPathIndex || overlayPlan.UsesPathIndex,
            usesIndex: basePlan.UsesIndex || overlayPlan.UsesIndex,
            canUseLiteralVerifier: basePlan.CanUseLiteralVerifier && overlayPlan.CanUseLiteralVerifier,
            canUseNonBacktracking: basePlan.CanUseNonBacktracking && overlayPlan.CanUseNonBacktracking,
            needsUnicodeDecode: basePlan.NeedsUnicodeDecode || overlayPlan.NeedsUnicodeDecode,
            mayRequireWholeDocumentVerification:
                basePlan.MayRequireWholeDocumentVerification ||
                overlayPlan.MayRequireWholeDocumentVerification,
            estimatedPathFilterCost:
                basePlan.EstimatedPathFilterCost +
                overlayPlan.EstimatedPathFilterCost,
            estimatedDirectScanCost:
                basePlan.EstimatedDirectScanCost +
                overlayPlan.EstimatedDirectScanCost,
            estimatedIndexedCost: SumNullable(
                basePlan.EstimatedIndexedCost,
                overlayPlan.EstimatedIndexedCost),
            estimatedCandidateDocuments:
                basePlan.EstimatedCandidateDocuments +
                overlayPlan.EstimatedCandidateDocuments,
            estimatedCost: estimatedCost,
            candidateKind: candidateKind,
            candidateCombination: candidateCombination,
            verifierKind: verifierKind,
            runnerUp: runnerUp,
            runnerUpEstimatedCostDelta: runnerUp is null
                ? null
                : Math.Max(0d, runnerUp.EstimatedCost - estimatedCost),
            timeoutPolicy: basePlan.TimeoutPolicy,
            plannerAlternatives: plannerAlternatives);
    }

    private static T MergeStrategy<T>(
        T left,
        int leftSegments,
        T right,
        int rightSegments,
        T mixed)
        where T : struct, Enum
    {
        if (leftSegments == 0)
        {
            return right;
        }

        if (rightSegments == 0)
        {
            return left;
        }

        return EqualityComparer<T>.Default.Equals(left, right) ? left : mixed;
    }

    private static PlannerAlternative[] MergePlannerAlternatives(
        IReadOnlyList<PlannerAlternative> left,
        IReadOnlyList<PlannerAlternative> right)
    {
        var sums = new Dictionary<(SearchCandidateKind CandidateKind, SearchCandidateCombination CandidateCombination, SearchVerifierKind VerifierKind, bool UsesIndex), PlannerAlternativeAccumulator>();
        Add(left);
        Add(right);
        return sums
            .Select(static pair => pair.Value.ToPlannerAlternative(
                pair.Key.CandidateKind,
                pair.Key.CandidateCombination,
                pair.Key.VerifierKind,
                pair.Key.UsesIndex))
            .OrderBy(static alternative => alternative.EstimatedCost)
            .ThenBy(static alternative => alternative.EstimatedCandidateDocuments)
            .ToArray();

        void Add(IReadOnlyList<PlannerAlternative> alternatives)
        {
            for (var i = 0; i < alternatives.Count; i++)
            {
                var alternative = alternatives[i];
                var key = (
                    alternative.CandidateKind,
                    alternative.CandidateCombination,
                    alternative.VerifierKind,
                    alternative.UsesIndex);
                ref var accumulator = ref CollectionsMarshal.GetValueRefOrAddDefault(sums, key, out _);
                accumulator.Add(alternative);
            }
        }
    }

    private static string[] MergeStrings(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (left.Count == 0)
        {
            return [.. right];
        }

        if (right.Count == 0)
        {
            return [.. left];
        }

        var merged = new List<string>(left.Count + right.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < left.Count; i++)
        {
            if (seen.Add(left[i]))
            {
                merged.Add(left[i]);
            }
        }

        for (var i = 0; i < right.Count; i++)
        {
            if (seen.Add(right[i]))
            {
                merged.Add(right[i]);
            }
        }

        return [.. merged];
    }

    private static SelectedNgram[] MergeSelectedNgrams(
        IReadOnlyList<SelectedNgram> left,
        IReadOnlyList<SelectedNgram> right)
    {
        if (left.Count == 0)
        {
            return [.. right];
        }

        if (right.Count == 0)
        {
            return [.. left];
        }

        var merged = new Dictionary<(string Text, int ByteLength), int>();
        for (var i = 0; i < left.Count; i++)
        {
            var key = (left[i].Text, left[i].ByteLength);
            merged.TryGetValue(key, out var documentFrequency);
            merged[key] = checked(documentFrequency + left[i].DocumentFrequency);
        }

        for (var i = 0; i < right.Count; i++)
        {
            var key = (right[i].Text, right[i].ByteLength);
            merged.TryGetValue(key, out var documentFrequency);
            merged[key] = checked(documentFrequency + right[i].DocumentFrequency);
        }

        return merged
            .Select(static pair => new SelectedNgram(pair.Key.Text, pair.Key.ByteLength, pair.Value))
            .OrderBy(static gram => gram.ByteLength)
            .ThenBy(static gram => gram.Text, StringComparer.Ordinal)
            .ToArray();
    }

    private static double? SumNullable(double? left, double? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left.Value + right.Value;
    }
}
