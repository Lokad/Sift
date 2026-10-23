using System.Runtime.InteropServices;
using Lokad.Sift;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal ref struct SearchPlanCandidateBuilder
{
    private readonly MappedSegment _segment;
    private readonly SegmentScope _scope;
    private readonly SearchQueryFeatures _features;
    private readonly SiftOptions _options;

    private SearchPlanCandidateBuilder(
        MappedSegment segment,
        SegmentScope scope,
        in SearchQueryFeatures features,
        SiftOptions options)
    {
        _segment = segment;
        _scope = scope;
        _features = features;
        _options = options;
    }

    public static List<SearchPlanCandidate> BuildCandidates(
        MappedSegment segment,
        SegmentScope scope,
        in SearchQueryFeatures features,
        SiftOptions options)
    {
        return new SearchPlanCandidateBuilder(segment, scope, in features, options).Build();
    }

    private List<SearchPlanCandidate> Build()
    {
        var segment = _segment;
        var scope = _scope;
        var features = _features;
        var options = _options;
        var candidates = new List<SearchPlanCandidate>(features.IsLiteralLike ? 2 : 3);
        if (features.IsLiteralLike)
        {
            var direct = LiteralPlan.Direct(scope.Count, scope.Bytes);
            candidates.Add(CreateLiteralContentPlan(direct));
            if (features.Literal is { } literal && literal.Needle is { } needle)
            {
                var indexed = LiteralQueryPlanner.TryCreateIndexedCandidate(
                    segment,
                    scope.Count,
                    scope.Bytes,
                    literal.Text,
                    needle,
                    features.ResolvedCaseMode,
                    selectAllGrams: false,
                    options);
                if (indexed.HasValue)
                {
                    candidates.Add(CreateLiteralContentPlan(indexed.Value));
                }
            }

            return candidates;
        }

        candidates.Add(CreateDirectRegexCandidate());
        if (features.ResolvedCaseMode == CaseMode.Insensitive && !features.RegexPlan.HasByteAnalysis)
        {
            // RegexOptions.IgnoreCase has Unicode equivalences that the byte indexes
            // do not encode. Only a byte-analyzed pattern can safely prune ASCII
            // documents while the search separately retains every non-ASCII document.
            return candidates;
        }

        var mandatoryPlans = new LiteralPlan?[features.MandatoryTexts.Length];
        for (var i = 0; i < mandatoryPlans.Length; i++)
        {
            mandatoryPlans[i] = SelectIndexedLiteralCandidate(
                features.MandatoryTexts[i],
                features.ResolvedCaseMode,
                selectAllGrams: true);
        }

        var leadingPlans = new LiteralPlan?[features.LeadingLiterals.Length];
        for (var i = 0; i < leadingPlans.Length; i++)
        {
            leadingPlans[i] = SelectIndexedLiteralCandidate(
                features.LeadingLiterals[i],
                features.ResolvedCaseMode,
                selectAllGrams: false);
        }

        if (features.HasMandatoryFragments)
        {
            var structuralUnion = TryCreateStructuredLeadingLiteralPlan(
                mandatoryPlans,
                leadingPlans);
            if (structuralUnion.HasValue)
            {
                candidates.Add(CreateRegexIndexedCandidate(
                    structuralUnion.Value.Plan,
                    structuralUnion.Value.EstimatedCandidates));
            }
        }

        if (!features.HasMandatoryFragments)
        {
            // Only exhaustive leading literals can exclude candidates. Advisory
            // seeds are lossy diagnostics and cannot establish branch coverage.
            var alternationPlan = TryCreateLeadingLiteralSetPlan(
                leadingPlans);
            if (alternationPlan.HasValue)
            {
                candidates.Add(CreateRegexIndexedCandidate(
                    alternationPlan.Value,
                    SearchPlanner.EstimateCandidateDocuments(
                        alternationPlan.Value.SelectedGrams,
                        scope.Count)));
            }

            return candidates;
        }

        var fragmentPlans = new List<LiteralPlan>(features.MandatoryTexts.Length);
        for (var i = 0; i < mandatoryPlans.Length; i++)
        {
            var current = mandatoryPlans[i];
            if (current is { IsMissingGram: true })
            {
                candidates.Add(CreateRegexIndexedCandidate(current.Value, estimatedCandidates: 0));
                return candidates;
            }

            if (current.HasValue)
            {
                fragmentPlans.Add(current.Value);
            }
        }

        var anchor = SelectAnchorPlan(fragmentPlans);
        if (anchor.UsesIndex)
        {
            var selected = MergeFragmentGrams(fragmentPlans, anchor);
            if (selected.Length > 0)
            {
                var estimatedCandidates = SearchPlanner.EstimateCandidateDocuments(selected, scope.Count);
                var indexed = LiteralPlan.Intersection(
                    scope.Count,
                    scope.Bytes,
                    anchor.SeedByteLength,
                    selected,
                    anchor.IndexPair,
                    anchor.GramLength);
                candidates.Add(CreateRegexIndexedCandidate(indexed, estimatedCandidates));
            }
        }

        return candidates;
    }

    private SearchPlanCandidate CreateLiteralContentPlan(in LiteralPlan literalPlan)
    {
        var selected = literalPlan.SelectedGrams;
        var directScanCost = Math.Max(literalPlan.ScopeBytes, literalPlan.ScopeDocuments) *
            _features.RegexPlan.DirectScanCostMultiplier;
        var seedByteLength = literalPlan.SeedByteLength;
        var estimatedCandidates = literalPlan.UsesIndex && literalPlan.SelectedGrams.Length > 0
            ? SearchPlanner.EstimateCandidateDocuments(literalPlan.SelectedGrams, literalPlan.ScopeDocuments)
            : literalPlan.IsMissingGram ? 0 : literalPlan.ScopeDocuments;
        double? estimatedIndexedCost = literalPlan.UsesIndex
            ? literalPlan.IsMissingGram
                ? 0d
                : SearchPlanner.EstimateIndexedCost(
                    seedByteLength,
                    estimatedCandidates,
                    literalPlan.SelectedGrams,
                    _features.RegexPlan)
            : null;
        var estimatedCost = literalPlan.UsesIndex
            ? literalPlan.IsMissingGram
                ? 0d
                : SearchPlanner.ShouldPreferLiteralDirectScan(
                    literalPlan.ScopeDocuments,
                    literalPlan.ScopeBytes,
                    seedByteLength,
                    literalPlan.SelectedGrams,
                    estimatedCandidates,
                    _options)
                    ? directScanCost + 1d
                    : estimatedIndexedCost ?? 0d
            : directScanCost;
        return new SearchPlanCandidate(
            literalPlan,
            SearchPlanKinds.GetCandidateKind(
                selected.Length == 0 ? 0 : selected[0].GramLength,
                literalPlan.UsesIndex),
            SearchPlanKinds.GetCandidateCombination(
                selected.Length,
                literalPlan.UsesIndex,
                literalPlan.CandidateMode is LiteralCandidateMode.Union or LiteralCandidateMode.UnionOfIntersections,
                literalPlan.CandidateMode == LiteralCandidateMode.UnionOfIntersections),
            SearchPlanKinds.GetVerifierKind(_features.Query, _features.RegexPlan),
            directScanCost,
            estimatedIndexedCost,
            estimatedCandidates,
            estimatedCost);
    }

    private SearchPlanCandidate CreateDirectRegexCandidate()
    {
        var directScanCost = SearchPlanner.EstimateDirectScanCost(_scope, _features.RegexPlan);
        return new SearchPlanCandidate(
            LiteralPlan.Direct(_scope.Count, _scope.Bytes),
            SearchCandidateKind.DirectScan,
            SearchCandidateCombination.None,
            GetVerifierKind(),
            directScanCost,
            null,
            _scope.Count,
            directScanCost);
    }

    private SearchPlanCandidate CreateRegexIndexedCandidate(
        in LiteralPlan literalPlan,
        double estimatedCandidates)
    {
        var selected = literalPlan.SelectedGrams;
        var includesUnicodeFallback =
            _features.Query.PatternMode == PatternMode.Regex &&
            _features.ResolvedCaseMode == CaseMode.Insensitive &&
            _features.RegexPlan.HasByteAnalysis &&
            _scope.NonAsciiDocuments > 0;
        if (includesUnicodeFallback)
        {
            estimatedCandidates = Math.Min(
                _scope.Count,
                estimatedCandidates + _scope.NonAsciiDocuments);
        }

        var directScanCost = Math.Max(literalPlan.ScopeBytes, literalPlan.ScopeDocuments) *
            _features.RegexPlan.DirectScanCostMultiplier;
        var indexCanRejectEntireScope = literalPlan.IsMissingGram && !includesUnicodeFallback;
        double? estimatedIndexedCost = indexCanRejectEntireScope
            ? 0d
            : SearchPlanner.EstimateIndexedCost(
                literalPlan.SeedByteLength,
                estimatedCandidates,
                literalPlan.SelectedGrams,
                _features.RegexPlan) +
              (includesUnicodeFallback
                  ? _scope.NonAsciiBytes * _features.RegexPlan.DirectScanCostMultiplier
                  : 0d);
        var estimatedCost = indexCanRejectEntireScope
            ? 0d
            : SearchPlanner.ShouldPreferRegexDirectScan(
                _scope,
                literalPlan.SelectedGrams,
                estimatedCandidates,
                directScanCost,
                estimatedIndexedCost ?? 0d,
                includesUnicodeFallback,
                _options)
                ? directScanCost + 1d
                : estimatedIndexedCost ?? 0d;
        return new SearchPlanCandidate(
            literalPlan,
            SearchPlanKinds.GetCandidateKind(
                selected.Length == 0 ? 0 : selected[0].GramLength,
                usesIndex: true),
            SearchPlanKinds.GetCandidateCombination(
                selected.Length,
                usesIndex: true,
                literalPlan.CandidateMode is LiteralCandidateMode.Union or LiteralCandidateMode.UnionOfIntersections,
                literalPlan.CandidateMode == LiteralCandidateMode.UnionOfIntersections),
            GetVerifierKind(),
            directScanCost,
            estimatedIndexedCost,
            estimatedCandidates,
            estimatedCost);
    }

    private SearchVerifierKind GetVerifierKind()
    {
        var verifier = SearchPlanKinds.GetVerifierKind(
            _features.Query,
            _features.RegexPlan);
        if (verifier != SearchVerifierKind.Utf8Regex || _scope.NonAsciiDocuments == 0) return verifier;
        return _scope.NonAsciiDocuments == _scope.Count
            ? SearchVerifierKind.RegexFallback
            : SearchVerifierKind.Utf8RegexWithUnicodeFallback;
    }

    private LiteralPlan SelectAnchorPlan(List<LiteralPlan> fragmentPlans)
    {
        var best = LiteralPlan.Direct(_scope.Count, _scope.Bytes);
        var bestScore = double.PositiveInfinity;
        foreach (var plan in fragmentPlans)
        {
            if (!plan.UsesIndex)
            {
                continue;
            }

            var score = SearchPlanner.EstimateIndexedCost(
                plan.SeedByteLength,
                SearchPlanner.EstimateCandidateDocuments(plan.SelectedGrams, _scope.Count),
                plan.SelectedGrams,
                _features.RegexPlan);
            if (score < bestScore)
            {
                best = plan;
                bestScore = score;
            }
        }

        return best;
    }

    private (LiteralPlan Plan, double EstimatedCandidates)? TryCreateStructuredLeadingLiteralPlan(
        LiteralPlan?[] mandatoryPlans,
        LiteralPlan?[] leadingPlans)
    {
        var leadingLiterals = _features.LeadingLiterals;
        if (leadingLiterals.Length < 2)
        {
            return null;
        }

        if (leadingLiterals.Length > _options.MaxSelectedNgrams)
        {
            return null;
        }

        var sharedFragments = new List<LiteralPlan>(mandatoryPlans.Length);
        for (var i = 0; i < mandatoryPlans.Length; i++)
        {
            var fragmentPlan = mandatoryPlans[i];
            if (fragmentPlan.HasValue && !fragmentPlan.Value.IsMissingGram && fragmentPlan.Value.SelectedGrams.Length > 0)
            {
                sharedFragments.Add(fragmentPlan.Value);
            }
        }

        var selected = new List<SelectedGram>(_options.MaxSelectedNgrams);
        var groupLengths = new List<int>(leadingLiterals.Length);
        IndexRegionPair? pair = null;
        var gramLength = 0;
        double estimatedCandidates = 0d;
        for (var i = 0; i < leadingLiterals.Length; i++)
        {
            var literalPlan = leadingPlans[i];
            if (!literalPlan.HasValue || literalPlan.Value.IsMissingGram || literalPlan.Value.SelectedGrams.Length == 0)
            {
                return null;
            }

            var indexed = literalPlan.Value;
            if (pair is null)
            {
                pair = indexed.IndexPair;
                gramLength = indexed.GramLength;
            }
            else if (!pair.Value.Equals(indexed.IndexPair) || gramLength != indexed.GramLength)
            {
                return null;
            }

            var remainingBranchMinimum = leadingLiterals.Length - i - 1;
            var branchCapacity = _options.MaxSelectedNgrams - selected.Count - remainingBranchMinimum;
            var branch = new List<SelectedGram>(branchCapacity);
            AddBranchGrams(branch, indexed.SelectedGrams, branchCapacity);
            for (var fragmentIndex = 0; fragmentIndex < sharedFragments.Count; fragmentIndex++)
            {
                if (sharedFragments[fragmentIndex].IndexPair.Equals(pair.Value))
                {
                    AddBranchGrams(
                        branch,
                        sharedFragments[fragmentIndex].SelectedGrams,
                        branchCapacity);
                }
            }

            if (branch.Count == 0)
            {
                return null;
            }

            branch.Sort(static (left, right) => left.DocFreq.CompareTo(right.DocFreq));
            estimatedCandidates += SearchPlanner.EstimateCandidateDocuments(
                CollectionsMarshal.AsSpan(branch),
                _scope.Count);
            groupLengths.Add(branch.Count);
            for (var branchIndex = 0; branchIndex < branch.Count; branchIndex++)
            {
                selected.Add(branch[branchIndex]);
            }
        }

        if (selected.Count == 0 || pair is null)
        {
            return null;
        }

        return (
            LiteralPlan.UnionOfIntersections(
                _scope.Count,
                _scope.Bytes,
                leadingLiterals.Min(static literal => literal.Utf8Bytes.Length),
                [.. selected],
                pair.Value,
                gramLength,
                [.. groupLengths]),
            Math.Clamp(estimatedCandidates, 1d, _scope.Count));
    }

    private LiteralPlan? TryCreateLeadingLiteralSetPlan(LiteralPlan?[] leadingPlans)
    {
        var leadingLiterals = _features.LeadingLiterals;
        if (leadingLiterals.Length == 0)
        {
            return null;
        }

        var selected = new List<SelectedGram>(Math.Min(
            leadingLiterals.Length,
            _options.MaxSelectedNgrams));
        IndexRegionPair? pair = null;
        var gramLength = 0;
        for (var i = 0; i < leadingLiterals.Length; i++)
        {
            var literalPlan = leadingPlans[i];
            if (!literalPlan.HasValue || literalPlan.Value.IsMissingGram || literalPlan.Value.SelectedGrams.Length == 0)
            {
                return null;
            }

            var indexed = literalPlan.Value;
            if (pair is null)
            {
                pair = indexed.IndexPair;
                gramLength = indexed.GramLength;
            }
            else if (!pair.Value.Equals(indexed.IndexPair) || gramLength != indexed.GramLength)
            {
                return null;
            }

            TryAddSelectedGram(selected, indexed.SelectedGrams[0]);
            // Visit every alternative even when earlier branches filled the budget:
            // later branches may share a gram, or make this union unsafe to use.
            if (selected.Count > _options.MaxSelectedNgrams) return null;
        }

        if (selected.Count == 0 || pair is null)
        {
            return null;
        }

        selected.Sort(static (left, right) => left.DocFreq.CompareTo(right.DocFreq));
        return LiteralPlan.Union(
            _scope.Count,
            _scope.Bytes,
            leadingLiterals.Min(static literal => literal.Utf8Bytes.Length),
            [.. selected],
            pair.Value,
            gramLength);
    }

    private static void AddBranchGrams(
        List<SelectedGram> branch,
        ReadOnlySpan<SelectedGram> grams,
        int maximumCount)
    {
        for (var i = 0; i < grams.Length && branch.Count < maximumCount; i++)
        {
            TryAddSelectedGram(branch, grams[i]);
        }
    }

    private LiteralPlan? SelectIndexedLiteralCandidate(
        PreparedSearchText literal,
        CaseMode resolvedCaseMode,
        bool selectAllGrams)
    {
        return LiteralQueryPlanner.TryCreateIndexedCandidate(
            _segment,
            _scope.Count,
            _scope.Bytes,
            literal.Text,
            literal.Utf8Bytes,
            resolvedCaseMode,
            selectAllGrams,
            _options);
    }

    private SelectedGram[] MergeFragmentGrams(
        List<LiteralPlan> fragmentPlans,
        LiteralPlan anchor)
    {
        var selected = new List<SelectedGram>(_options.MaxSelectedNgrams);
        foreach (var plan in fragmentPlans)
        {
            if (!plan.UsesIndex || plan.SelectedGrams.Length == 0 || !plan.IndexPair.Equals(anchor.IndexPair))
            {
                continue;
            }

            AddBranchGrams(selected, plan.SelectedGrams, _options.MaxSelectedNgrams);
        }

        if (selected.Count == 0)
        {
            return [];
        }

        selected.Sort(static (left, right) => left.DocFreq.CompareTo(right.DocFreq));
        var targetCandidates = SearchPlanner.GetTargetCandidateDocuments(
            anchor.ScopeDocuments,
            _options);
        if (targetCandidates == int.MaxValue)
        {
            return [.. selected];
        }

        for (var count = 1; count < selected.Count; count++)
        {
            if (SearchPlanner.EstimateCandidateDocuments(CollectionsMarshal.AsSpan(selected)[..count], anchor.ScopeDocuments) <= targetCandidates)
            {
                return CollectionsMarshal.AsSpan(selected)[..count].ToArray();
            }
        }

        return [.. selected];
    }

    private static void TryAddSelectedGram(List<SelectedGram> selected, SelectedGram candidate)
    {
        for (var i = 0; i < selected.Count; i++)
        {
            if (selected[i].Gram == candidate.Gram && selected[i].GramLength == candidate.GramLength)
            {
                return;
            }
        }

        selected.Add(candidate);
    }
}

