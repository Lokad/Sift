using System.Runtime.InteropServices;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal sealed class SearchPlanExplainer(
    IReadOnlyList<SegmentManifestEntry> segments,
    SiftOptions options,
    Func<SegmentManifestEntry, SegmentCache> openSegment)
{
    public SearchPlan Explain(
        SearchQuery query,
        PathFilter pathFilter,
        PreparedSearchQuery preparedQuery)
    {
        var resolvedCaseMode = preparedQuery.ResolvedCaseMode;
        var matcher = PathFilterMatcher.Prepare(pathFilter);
        var regexPlan = preparedQuery.RegexPlan;
        var anchors = preparedQuery.Anchors;
        var mandatoryFragments = preparedQuery.Anchors.MandatoryTexts;
        var advisorySeeds = preparedQuery.Anchors.AdvisoryTexts;
        var scopeDocuments = 0;
        long scopeBytes = 0;
        var selected = new Dictionary<(uint PackedValue, int ByteLength), int>();
        var usesIndex = false;
        var usesUnicodeFallback = false;
        SearchCandidateKind? candidateKind = null;
        SearchCandidateCombination? candidateCombination = null;
        SearchVerifierKind? verifierKind = null;
        var estimatedPathFilterCost = 0d;
        var estimatedDirectScanCost = 0d;
        double? estimatedIndexedCost = null;
        var estimatedCandidateDocuments = 0d;
        var estimatedCost = 0d;
        var segmentCount = 0;
        var alternativeSums = new Dictionary<(SearchCandidateKind CandidateKind, SearchCandidateCombination CandidateCombination, SearchVerifierKind VerifierKind, bool UsesIndex), PlannerAlternativeAccumulator>();

        foreach (var entry in segments)
        {
            var cache = openSegment(entry);
            var segmentPlan = SearchPlanner.CreateDiagnosticPlan(
                cache,
                matcher,
                query,
                preparedQuery,
                options,
                CancellationToken.None);
            scopeDocuments += segmentPlan.Scope.Count;
            scopeBytes += segmentPlan.Scope.Bytes;
            segmentCount++;
            var chosenPlan = segmentPlan.ContentPlan.ChosenPlan;
            usesUnicodeFallback |=
                chosenPlan.VerifierKind ==
                SearchVerifierKind.Utf8RegexWithUnicodeFallback;
            usesIndex |= chosenPlan.CandidatePlan.UsesIndex;
            candidateKind = Merge(candidateKind, chosenPlan.CandidateKind, SearchCandidateKind.Mixed);
            candidateCombination = Merge(
                candidateCombination,
                chosenPlan.CandidateCombination,
                SearchCandidateCombination.Mixed);
            verifierKind = Merge(verifierKind, chosenPlan.VerifierKind, SearchVerifierKind.Mixed);
            estimatedPathFilterCost += segmentPlan.EstimatedPathFilterCost;
            estimatedDirectScanCost += chosenPlan.EstimatedDirectScanCost;
            if (chosenPlan.EstimatedIndexedCost is double indexedCost)
            {
                estimatedIndexedCost = (estimatedIndexedCost ?? 0d) + indexedCost;
            }

            estimatedCandidateDocuments += chosenPlan.EstimatedCandidateDocuments;
            estimatedCost += chosenPlan.EstimatedCost;
            foreach (var gram in chosenPlan.CandidatePlan.SelectedGrams)
            {
                SelectedNgramMerger.Add(selected, gram);
            }

            foreach (var alternative in segmentPlan.ContentPlan.Alternatives)
            {
                var key = (
                    alternative.CandidateKind,
                    alternative.CandidateCombination,
                    alternative.VerifierKind,
                    alternative.CandidatePlan.UsesIndex);
                ref var accumulator = ref CollectionsMarshal.GetValueRefOrAddDefault(
                    alternativeSums,
                    key,
                    out _);
                accumulator.Add(alternative, segmentPlan.Scope);
            }
        }

        var patternKind = SearchPlanKinds.GetPatternKind(query, regexPlan);
        var canUseLiteralVerifier = query.PatternMode == PatternMode.Literal || regexPlan.IsExactLiteral;
        var canUseNonBacktracking = query.PatternMode == PatternMode.Regex && regexPlan.CanUseNonBacktracking;
        var needsUnicodeDecode =
            NeedsUnicodeDecode(
                query,
                preparedQuery.Pattern,
                preparedQuery.MandatoryTexts,
                resolvedCaseMode,
                regexPlan) ||
            usesUnicodeFallback;
        var mayRequireWholeDocumentVerification =
            query.PatternMode == PatternMode.Regex && !regexPlan.IsExactLiteral ||
            query.WholeLine ||
            query.Multiline;
        var selectedNgrams = SelectedNgramMerger.Materialize(selected);
        var resolvedCandidateKind = candidateKind ??
            SearchPlanner.GetCandidateKind(
                selectedNgrams.Length == 0 ? 0 : selectedNgrams[0].ByteLength,
                usesIndex);
        var resolvedCandidateCombination = candidateCombination ??
            SearchPlanner.GetCandidateCombination(
                selectedNgrams.Length,
                usesIndex,
                unionCandidates: false,
                branchUnion: false);
        var resolvedVerifierKind = verifierKind ??
            SearchPlanKinds.GetVerifierKind(query, regexPlan);
        var plannerAlternatives = alternativeSums
            .Select(static pair => pair.Value.ToPlannerAlternative(
                pair.Key.CandidateKind,
                pair.Key.CandidateCombination,
                pair.Key.VerifierKind,
                pair.Key.UsesIndex))
            .OrderBy(static alternative => alternative.EstimatedCost)
            .ThenBy(static alternative => alternative.EstimatedCandidateDocuments)
            .ToArray();
        var runnerUp = plannerAlternatives
            .FirstOrDefault(alternative =>
                alternative.SegmentCount == segmentCount &&
                (alternative.CandidateKind != resolvedCandidateKind ||
                 alternative.CandidateCombination != resolvedCandidateCombination ||
                 alternative.VerifierKind != resolvedVerifierKind));

        return new SearchPlan(
            patternKind: patternKind,
            resolvedCaseMode: resolvedCaseMode,
            scopeDocumentCount: scopeDocuments,
            scopeByteCount: scopeBytes,
            segmentCount: segmentCount,
            mandatoryFragments: mandatoryFragments,
            advisorySeeds: advisorySeeds,
            selectedNgrams: selectedNgrams,
            usesPathPrefixFilter: matcher.PathPrefix is not null,
            usesPathRegexFilter: !string.IsNullOrWhiteSpace(pathFilter.PathRegex),
            usesIncludeGlobs: pathFilter.IncludeGlobs.Count > 0,
            usesExcludeGlobs: pathFilter.ExcludeGlobs.Count > 0,
            usesPathIndex: matcher.UsesPathIndex,
            usesIndex: usesIndex,
            canUseLiteralVerifier: canUseLiteralVerifier,
            canUseNonBacktracking: canUseNonBacktracking,
            needsUnicodeDecode: needsUnicodeDecode,
            mayRequireWholeDocumentVerification: mayRequireWholeDocumentVerification,
            estimatedPathFilterCost: estimatedPathFilterCost,
            estimatedDirectScanCost: estimatedDirectScanCost,
            estimatedIndexedCost: estimatedIndexedCost,
            estimatedCandidateDocuments: estimatedCandidateDocuments,
            estimatedCost: estimatedCost,
            candidateKind: resolvedCandidateKind,
            candidateCombination: resolvedCandidateCombination,
            verifierKind: resolvedVerifierKind,
            runnerUp: runnerUp,
            runnerUpEstimatedCostDelta: runnerUp is null
                ? null
                : Math.Max(0d, runnerUp.EstimatedCost - estimatedCost),
            regexTimeoutPolicy: query.PatternMode == PatternMode.Regex
                ? query.RegexTimeoutPolicy
                : RegexTimeoutPolicy.StopSearch,
            plannerAlternatives: plannerAlternatives);

        static T Merge<T>(T? aggregate, T value, T mixed)
            where T : struct, Enum
        {
            return aggregate is null || EqualityComparer<T>.Default.Equals(aggregate.Value, value)
                ? value
                : mixed;
        }

        static bool NeedsUnicodeDecode(
            SearchQuery query,
            PreparedSearchText pattern,
            PreparedSearchText[] mandatoryFragments,
            CaseMode resolvedCaseMode,
            in RegexExecutionPlan regexPlan)
        {
            if (query.PatternMode == PatternMode.Regex && !regexPlan.IsExactLiteral && !regexPlan.UsesUtf8Regex)
            {
                return true;
            }

            if (query.PatternMode == PatternMode.Regex && (regexPlan.IsExactLiteral || regexPlan.UsesUtf8Regex))
            {
                return false;
            }

            if (query.Multiline || query.DotAll)
            {
                return true;
            }

            if (query.WholeLine)
            {
                return !pattern.IsAscii;
            }

            if (resolvedCaseMode == CaseMode.Sensitive)
            {
                return false;
            }

            for (var i = 0; i < mandatoryFragments.Length; i++)
            {
                if (!mandatoryFragments[i].IsAscii)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

internal struct PlannerAlternativeAccumulator
{
    public double EstimatedDirectScanCost;
    public double? EstimatedIndexedCost;
    public double EstimatedCandidateDocuments;
    public double EstimatedCost;
    public int SegmentCount;
    public int ScopeDocumentCount;
    public long ScopeByteCount;

    public void Add(in SearchPlanCandidate plan, in SegmentScope scope)
    {
        EstimatedDirectScanCost += plan.EstimatedDirectScanCost;
        if (plan.EstimatedIndexedCost is double estimatedIndexedCost)
        {
            EstimatedIndexedCost = (EstimatedIndexedCost ?? 0d) + estimatedIndexedCost;
        }

        EstimatedCandidateDocuments += plan.EstimatedCandidateDocuments;
        EstimatedCost += plan.EstimatedCost;
        SegmentCount++;
        ScopeDocumentCount += scope.Count;
        ScopeByteCount += scope.Bytes;
    }

    public void Add(PlannerAlternative alternative)
    {
        EstimatedDirectScanCost += alternative.EstimatedDirectScanCost;
        if (alternative.EstimatedIndexedCost is double estimatedIndexedCost)
        {
            EstimatedIndexedCost = (EstimatedIndexedCost ?? 0d) + estimatedIndexedCost;
        }

        EstimatedCandidateDocuments += alternative.EstimatedCandidateDocuments;
        EstimatedCost += alternative.EstimatedCost;
        SegmentCount += alternative.SegmentCount;
        ScopeDocumentCount += alternative.ScopeDocumentCount;
        ScopeByteCount += alternative.ScopeByteCount;
    }

    public PlannerAlternative ToPlannerAlternative(
        SearchCandidateKind candidateKind,
        SearchCandidateCombination candidateCombination,
        SearchVerifierKind verifierKind,
        bool usesIndex)
    {
        return new PlannerAlternative(
            candidateKind: candidateKind,
            candidateCombination: candidateCombination,
            verifierKind: verifierKind,
            usesIndex: usesIndex,
            estimatedDirectScanCost: EstimatedDirectScanCost,
            estimatedIndexedCost: EstimatedIndexedCost,
            estimatedCandidateDocuments: EstimatedCandidateDocuments,
            estimatedCost: EstimatedCost,
            segmentCount: SegmentCount,
            scopeDocumentCount: ScopeDocumentCount,
            scopeByteCount: ScopeByteCount);
    }
}
