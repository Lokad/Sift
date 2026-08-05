using System.Runtime.InteropServices;
using Lokad.Sift;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal static class SearchPlanCandidateBuilder
{
    public static List<SearchPlanCandidate> Enumerate(MappedSegment segment, SegmentScope scope, in SearchQueryFeatures features, SiftOptions options)
    {
        var candidates = new List<SearchPlanCandidate>(4);
        if (features.IsLiteralLike)
        {
            if (features.Literal is null)
            {
                candidates.Add(CreateLiteralContentPlan(
                    in features,
                    LiteralPlan.Direct(scope.Count, scope.Bytes),
                    options));
                return candidates;
            }

            foreach (var literalPlan in LiteralQueryPlanner.Enumerate(segment, scope.Count, scope.Bytes, features.Literal.Text, features.Literal.Needle, features.ResolvedCaseMode, selectAllGrams: false, options))
            {
                candidates.Add(CreateLiteralContentPlan(in features, literalPlan, options));
            }

            return candidates;
        }

        candidates.Add(CreateDirectRegexCandidate(in features, scope));
        if (features.ResolvedCaseMode == CaseMode.Insensitive && !features.RegexPlan.UsesUtf8Regex)
        {
            // RegexOptions.IgnoreCase has Unicode equivalences that the byte indexes
            // do not encode. Only a byte-compiled pattern can safely prune ASCII
            // documents while the search separately retains every non-ASCII document.
            return candidates;
        }

        var mandatoryPlans = new LiteralPlan?[features.MandatoryTexts.Length];
        for (var i = 0; i < mandatoryPlans.Length; i++)
        {
            mandatoryPlans[i] = SelectIndexedLiteralCandidate(
                segment,
                scope,
                features.MandatoryTexts[i],
                features.ResolvedCaseMode,
                selectAllGrams: true,
                options);
        }

        var leadingPlans = new LiteralPlan?[features.LeadingLiterals.Length];
        for (var i = 0; i < leadingPlans.Length; i++)
        {
            leadingPlans[i] = SelectIndexedLiteralCandidate(
                segment,
                scope,
                features.LeadingLiterals[i],
                features.ResolvedCaseMode,
                selectAllGrams: false,
                options);
        }

        if (features.HasMandatoryFragments)
        {
            var structuralUnion = TryCreateStructuredLeadingLiteralPlan(
                scope,
                mandatoryPlans,
                features.LeadingLiterals,
                leadingPlans,
                options);
            if (structuralUnion.HasValue)
            {
                candidates.Add(CreateRegexIndexedCandidate(in features, scope, structuralUnion.Value.Plan, structuralUnion.Value.EstimatedCandidates, options));
            }
        }

        if (!features.HasMandatoryFragments)
        {
            if (features.RegexPlan.Kind == RegexExecutionKind.Fallback && features.AdvisoryTexts.Length > 0)
            {
                var advisoryPlan = TryCreateLiteralSetPlan(segment, scope, features.Anchors.AdvisoryCaseMode, features.AdvisoryTexts, options, isAdvisory: true);
                if (advisoryPlan.HasValue)
                {
                    candidates.Add(CreateRegexIndexedCandidate(in features, scope, advisoryPlan.Value, SearchPlanner.EstimateCandidateUpperBound(advisoryPlan.Value.SelectedGrams, scope.Count), options));
                }
            }

            var alternationPlan = TryCreateLeadingLiteralSetPlan(
                scope,
                features.LeadingLiterals,
                leadingPlans,
                options);
            if (alternationPlan.HasValue)
            {
                candidates.Add(CreateRegexIndexedCandidate(in features, scope, alternationPlan.Value, SearchPlanner.EstimateCandidateUpperBound(alternationPlan.Value.SelectedGrams, scope.Count), options));
            }

            return candidates;
        }

        var fragmentPlans = new List<LiteralPlan>(features.MandatoryTexts.Length);
        for (var i = 0; i < mandatoryPlans.Length; i++)
        {
            var current = mandatoryPlans[i];
            if (current is { IsMissingGram: true })
            {
                candidates.Add(CreateRegexIndexedCandidate(in features, scope, current.Value, estimatedCandidates: 0, options));
                return candidates;
            }

            if (current.HasValue)
            {
                fragmentPlans.Add(current.Value);
            }
        }

        var anchor = SelectAnchorPlan(fragmentPlans, scope, features.RegexPlan);
        if (anchor.UsesIndex)
        {
            var selected = MergeFragmentGrams(fragmentPlans, anchor, options);
            if (selected.Length > 0)
            {
                var estimatedCandidates = SearchPlanner.EstimateCandidateUpperBound(selected, scope.Count);
                var indexed = LiteralPlan.Intersection(
                    scope.Count,
                    scope.Bytes,
                    anchor.SeedByteLength,
                    selected,
                    anchor.IndexPair,
                    anchor.GramLength);
                candidates.Add(CreateRegexIndexedCandidate(in features, scope, indexed, estimatedCandidates, options));
            }
        }

        return candidates;
    }

    private static SearchPlanCandidate CreateLiteralContentPlan(in SearchQueryFeatures features, in LiteralPlan literalPlan, SiftOptions options)
    {
        var selected = SearchPlanner.ToSelectedNgrams(literalPlan.SelectedGrams);
        var directScanCost = Math.Max(literalPlan.ScopeBytes, literalPlan.ScopeDocuments) * features.RegexPlan.DirectScanCostMultiplier;
        var seedByteLength = literalPlan.SeedByteLength;
        var estimatedCandidates = literalPlan.UsesIndex && literalPlan.SelectedGrams.Length > 0
            ? SearchPlanner.EstimateCandidateUpperBound(literalPlan.SelectedGrams, literalPlan.ScopeDocuments)
            : literalPlan.IsMissingGram ? 0 : literalPlan.ScopeDocuments;
        double? estimatedIndexedCost = literalPlan.UsesIndex
            ? literalPlan.IsMissingGram
                ? 0d
                : SearchPlanner.EstimateIndexedCost(
                    seedByteLength,
                    estimatedCandidates,
                    literalPlan.SelectedGrams,
                    features.RegexPlan)
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
                    options)
                    ? directScanCost + 1d
                    : estimatedIndexedCost ?? 0d
            : directScanCost;
        return new SearchPlanCandidate(
            literalPlan,
            SearchPlanner.GetCandidateKind(selected, literalPlan.UsesIndex),
            SearchPlanner.GetCandidateCombination(
                selected,
                literalPlan.UsesIndex,
                literalPlan.CandidateMode is LiteralCandidateMode.Union or LiteralCandidateMode.UnionOfIntersections,
                literalPlan.CandidateMode == LiteralCandidateMode.UnionOfIntersections),
            SearchPlanKinds.GetVerifierKind(features.Query, features.RegexPlan),
            directScanCost,
            estimatedIndexedCost,
            estimatedCandidates,
            selected,
            estimatedCost);
    }

    private static SearchPlanCandidate CreateDirectRegexCandidate(in SearchQueryFeatures features, SegmentScope scope)
    {
        var directScanCost = SearchPlanner.EstimateDirectScanCost(scope, features.RegexPlan);
        return new SearchPlanCandidate(
            LiteralPlan.Direct(scope.Count, scope.Bytes),
            SearchCandidateKind.DirectScan,
            SearchCandidateCombination.None,
            GetVerifierKind(features, scope),
            directScanCost,
            null,
            scope.Count,
            [],
            directScanCost);
    }

    private static SearchPlanCandidate CreateRegexIndexedCandidate(in SearchQueryFeatures features, SegmentScope scope, in LiteralPlan literalPlan, double estimatedCandidates, SiftOptions options)
    {
        var selected = SearchPlanner.ToSelectedNgrams(literalPlan.SelectedGrams);
        var includesUnicodeFallback =
            features.Query.PatternMode == PatternMode.Regex &&
            features.ResolvedCaseMode == CaseMode.Insensitive &&
            features.RegexPlan.UsesUtf8Regex &&
            scope.NonAsciiDocuments > 0;
        if (includesUnicodeFallback)
        {
            estimatedCandidates = Math.Min(
                scope.Count,
                estimatedCandidates + scope.NonAsciiDocuments);
        }

        var directScanCost = Math.Max(literalPlan.ScopeBytes, literalPlan.ScopeDocuments) * features.RegexPlan.DirectScanCostMultiplier;
        var indexCanRejectEntireScope = literalPlan.IsMissingGram && !includesUnicodeFallback;
        double? estimatedIndexedCost = indexCanRejectEntireScope
            ? 0d
            : SearchPlanner.EstimateIndexedCost(
                literalPlan.SeedByteLength,
                estimatedCandidates,
                literalPlan.SelectedGrams,
                features.RegexPlan) +
              (includesUnicodeFallback
                  ? scope.NonAsciiBytes * features.RegexPlan.DirectScanCostMultiplier
                  : 0d);
        var estimatedCost = indexCanRejectEntireScope
            ? 0d
            : SearchPlanner.ShouldPreferRegexDirectScan(
                scope,
                literalPlan.SelectedGrams,
                estimatedCandidates,
                directScanCost,
                estimatedIndexedCost ?? 0d,
                includesUnicodeFallback,
                options)
                ? directScanCost + 1d
                : estimatedIndexedCost ?? 0d;
        return new SearchPlanCandidate(
            literalPlan,
            SearchPlanner.GetCandidateKind(selected, usesIndex: true),
            SearchPlanner.GetCandidateCombination(
                selected,
                usesIndex: true,
                literalPlan.CandidateMode is LiteralCandidateMode.Union or LiteralCandidateMode.UnionOfIntersections,
                literalPlan.CandidateMode == LiteralCandidateMode.UnionOfIntersections),
            GetVerifierKind(features, scope),
            directScanCost,
            estimatedIndexedCost,
            estimatedCandidates,
            selected,
            estimatedCost);
    }

    private static SearchVerifierKind GetVerifierKind(
        in SearchQueryFeatures features,
        SegmentScope scope)
    {
        var verifier = SearchPlanKinds.GetVerifierKind(
            features.Query,
            features.RegexPlan);
        return verifier == SearchVerifierKind.Utf8Regex && scope.NonAsciiDocuments > 0
            ? SearchVerifierKind.Utf8RegexWithUnicodeFallback
            : verifier;
    }

    private static LiteralPlan SelectAnchorPlan(List<LiteralPlan> fragmentPlans, SegmentScope scope, in RegexExecutionPlan regexPlan)
    {
        var best = LiteralPlan.Direct(scope.Count, scope.Bytes);
        var bestScore = double.PositiveInfinity;
        foreach (var plan in fragmentPlans)
        {
            if (!plan.UsesIndex)
            {
                continue;
            }

            var score = SearchPlanner.EstimateIndexedCost(plan.SeedByteLength, SearchPlanner.EstimateCandidateUpperBound(plan.SelectedGrams, scope.Count), plan.SelectedGrams, regexPlan);
            if (score < bestScore)
            {
                best = plan;
                bestScore = score;
            }
        }

        return best;
    }

    private static (LiteralPlan Plan, double EstimatedCandidates)? TryCreateStructuredLeadingLiteralPlan(
        SegmentScope scope,
        LiteralPlan?[] mandatoryPlans,
        PreparedSearchText[] leadingLiterals,
        LiteralPlan?[] leadingPlans,
        SiftOptions options)
    {
        if (leadingLiterals.Length < 2)
        {
            return null;
        }

        if (leadingLiterals.Length > options.MaxSelectedNgrams)
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

        var selected = new List<SelectedGram>(options.MaxSelectedNgrams);
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
            var branchCapacity = options.MaxSelectedNgrams - selected.Count - remainingBranchMinimum;
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
            estimatedCandidates += SearchPlanner.EstimateCandidateUpperBound(CollectionsMarshal.AsSpan(branch), scope.Count);
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
                scope.Count,
                scope.Bytes,
                leadingLiterals.Min(static literal => literal.Utf8Bytes.Length),
                [.. selected],
                pair.Value,
                gramLength,
                [.. groupLengths]),
            Math.Clamp(estimatedCandidates, 1d, scope.Count));
    }

    private static LiteralPlan? TryCreateLeadingLiteralSetPlan(
        SegmentScope scope,
        PreparedSearchText[] leadingLiterals,
        LiteralPlan?[] leadingPlans,
        SiftOptions options)
    {
        if (leadingLiterals.Length == 0)
        {
            return null;
        }

        var selected = new List<SelectedGram>(Math.Min(leadingLiterals.Length, options.MaxSelectedNgrams));
        IndexRegionPair? pair = null;
        var gramLength = 0;
        for (var i = 0; i < leadingLiterals.Length && selected.Count < options.MaxSelectedNgrams; i++)
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
        }

        if (selected.Count == 0 || pair is null)
        {
            return null;
        }

        selected.Sort(static (left, right) => left.DocFreq.CompareTo(right.DocFreq));
        return LiteralPlan.Union(
            scope.Count,
            scope.Bytes,
            leadingLiterals.Min(static literal => literal.Utf8Bytes.Length),
            [.. selected],
            pair.Value,
            gramLength);
    }

    private static LiteralPlan? TryCreateLiteralSetPlan(MappedSegment segment, SegmentScope scope, CaseMode resolvedCaseMode, PreparedSearchText[] literals, SiftOptions options, bool isAdvisory)
    {
        if (literals.Length == 0)
        {
            return null;
        }

        var selected = new List<SelectedGram>(Math.Min(literals.Length, options.MaxSelectedNgrams));
        IndexRegionPair? pair = null;
        var gramLength = 0;
        var foundAny = false;
        for (var i = 0; i < literals.Length && selected.Count < options.MaxSelectedNgrams; i++)
        {
            if (literals[i].Text.Length < 3 || IsPoisonousSeedText(literals[i].Text))
            {
                continue;
            }

            var literalPlan = SelectIndexedLiteralCandidate(segment, scope, literals[i], resolvedCaseMode, selectAllGrams: false, options);
            if (!literalPlan.HasValue || literalPlan.Value.SelectedGrams.Length == 0)
            {
                continue;
            }

            var indexed = literalPlan.Value;
            if (isAdvisory && IsPoisonousSeedPlan(indexed, scope.Count, options))
            {
                continue;
            }

            if (indexed.IsMissingGram)
            {
                foundAny = true;
                continue;
            }

            if (pair is null)
            {
                pair = indexed.IndexPair;
                gramLength = indexed.GramLength;
            }
            else if (!pair.Value.Equals(indexed.IndexPair) || gramLength != indexed.GramLength)
            {
                continue;
            }

            TryAddSelectedGram(selected, indexed.SelectedGrams[0]);
            foundAny = true;
        }

        if (!foundAny)
        {
            return null;
        }

        if (selected.Count == 0 || pair is null)
        {
            return LiteralPlan.Missing(scope.Count, scope.Bytes, 0, default, 3);
        }

        selected.Sort(static (left, right) => left.DocFreq.CompareTo(right.DocFreq));
        return LiteralPlan.Union(
            scope.Count,
            scope.Bytes,
            gramLength,
            [.. selected],
            pair.Value,
            gramLength);
    }

    private static bool IsPoisonousSeedText(string value)
    {
        var hasLetterOrDigit = false;
        var punctuationCount = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsLetterOrDigit(value[i]))
            {
                hasLetterOrDigit = true;
            }
            else if (!char.IsWhiteSpace(value[i]))
            {
                punctuationCount++;
            }
        }

        if (!hasLetterOrDigit)
        {
            return true;
        }

        return punctuationCount > 0 && punctuationCount * 2 >= value.Length;
    }

    private static bool IsPoisonousSeedPlan(in LiteralPlan plan, int scopeDocuments, SiftOptions options)
    {
        if (plan.IsMissingGram || plan.SelectedGrams.Length == 0 || scopeDocuments <= 0)
        {
            return true;
        }

        var density = (double)plan.SelectedGrams[0].DocFreq / scopeDocuments;
        var densityThreshold = options.DirectScanCandidateDensityThreshold > 0
            ? Math.Min(options.DirectScanCandidateDensityThreshold * 0.5d, 0.35d)
            : 0.35d;
        return density >= densityThreshold;
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

    private static LiteralPlan? SelectIndexedLiteralCandidate(MappedSegment segment, SegmentScope scope, PreparedSearchText literal, CaseMode resolvedCaseMode, bool selectAllGrams, SiftOptions options)
        => SelectIndexedLiteralCandidate(segment, scope, literal.Text, literal.Utf8Bytes, resolvedCaseMode, selectAllGrams, options);

    private static LiteralPlan? SelectIndexedLiteralCandidate(MappedSegment segment, SegmentScope scope, string literal, ReadOnlySpan<byte> utf8Bytes, CaseMode resolvedCaseMode, bool selectAllGrams, SiftOptions options)
    {
        return LiteralQueryPlanner.TryCreateIndexedCandidate(
            segment,
            scope.Count,
            scope.Bytes,
            literal,
            utf8Bytes,
            resolvedCaseMode,
            selectAllGrams,
            options);
    }

    private static SelectedGram[] MergeFragmentGrams(List<LiteralPlan> fragmentPlans, LiteralPlan anchor, SiftOptions options)
    {
        var selected = new List<SelectedGram>(options.MaxSelectedNgrams);
        foreach (var plan in fragmentPlans)
        {
            if (!plan.UsesIndex || plan.SelectedGrams.Length == 0 || !plan.IndexPair.Equals(anchor.IndexPair))
            {
                continue;
            }

            AddBranchGrams(selected, plan.SelectedGrams, options.MaxSelectedNgrams);
        }

        if (selected.Count == 0)
        {
            return [];
        }

        selected.Sort(static (left, right) => left.DocFreq.CompareTo(right.DocFreq));
        var targetCandidates = SearchPlanner.GetTargetCandidateDocuments(anchor.ScopeDocuments, options);
        if (targetCandidates == int.MaxValue)
        {
            return [.. selected];
        }

        for (var count = 1; count < selected.Count; count++)
        {
            if (SearchPlanner.EstimateCandidateUpperBound(CollectionsMarshal.AsSpan(selected)[..count], anchor.ScopeDocuments) <= targetCandidates)
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

