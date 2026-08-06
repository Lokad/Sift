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
            // These arrays describe the query, not a corpus generation.
            mandatoryFragments: [.. basePlan.MandatoryFragments],
            advisorySeeds: [.. basePlan.AdvisorySeeds],
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
            regexTimeoutPolicy: basePlan.RegexTimeoutPolicy,
            plannerAlternatives: plannerAlternatives);
        static T MergeStrategy<T>(
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

        static PlannerAlternative[] MergePlannerAlternatives(
        IReadOnlyList<PlannerAlternative> left,
        IReadOnlyList<PlannerAlternative> right)
        {
            var sums = new Dictionary<PlannerAlternativeKey, PlannerAlternativeAccumulator>();
            Add(left);
            Add(right);
            return PlannerAlternativeAggregation.Materialize(sums);

            void Add(IReadOnlyList<PlannerAlternative> alternatives)
            {
                for (var i = 0; i < alternatives.Count; i++)
                {
                    var alternative = alternatives[i];
                    PlannerAlternativeAggregation.Add(sums, alternative);
                }
            }
        }

        static SelectedNgram[] MergeSelectedNgrams(
        IReadOnlyList<SelectedNgram> left,
        IReadOnlyList<SelectedNgram> right)
        {
            return SelectedNgramMerger.Merge(left, right);
        }

        static double? SumNullable(double? left, double? right)
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

}
