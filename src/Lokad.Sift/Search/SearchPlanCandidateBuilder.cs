using System.Runtime.InteropServices;
using System.Text;
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
            foreach (var literalPlan in LiteralQueryPlanner.Enumerate(segment, scope.Ordinals, scope.Bytes, features.RegexPlan.ExactLiteral ?? features.Query.Pattern, features.ResolvedCaseMode, options))
            {
                candidates.Add(CreateLiteralContentPlan(in features, literalPlan, options));
            }

            return candidates;
        }

        candidates.Add(CreateDirectRegexCandidate(in features, scope));
        var structuralUnion = TryCreateStructuredLeadingLiteralPlan(segment, scope, features.ResolvedCaseMode, features.Anchors.GetMandatoryTexts(), features.RegexPlan, options);
        if (structuralUnion.HasValue)
        {
            candidates.Add(CreateRegexIndexedCandidate(in features, scope, structuralUnion.Value.Plan, structuralUnion.Value.EstimatedCandidates, options));
        }

        if (!features.HasMandatoryFragments)
        {
            if (features.RegexPlan.Kind == RegexExecutionKind.Fallback && features.Anchors.AdvisoryAnchors.Length > 0)
            {
                var advisoryPlan = TryCreateLiteralSetPlan(segment, scope, features.Anchors.AdvisoryCaseMode, features.Anchors.GetAdvisoryTexts(), options, isAdvisory: true);
                if (advisoryPlan.HasValue)
                {
                    candidates.Add(CreateRegexIndexedCandidate(in features, scope, advisoryPlan.Value, SearchPlanner.EstimateCandidateUpperBound(advisoryPlan.Value.SelectedGrams, scope.Ordinals.Length), options));
                }
            }

            var alternationPlan = TryCreateLeadingLiteralSetPlan(segment, scope, features.ResolvedCaseMode, features.RegexPlan, options);
            if (alternationPlan.HasValue)
            {
                candidates.Add(CreateRegexIndexedCandidate(in features, scope, alternationPlan.Value, SearchPlanner.EstimateCandidateUpperBound(alternationPlan.Value.SelectedGrams, scope.Ordinals.Length), options));
            }

            return candidates;
        }

        var fragmentPlans = new List<LiteralPlan>(features.Anchors.MandatoryAnchors.Length);
        foreach (var fragment in features.Anchors.MandatoryAnchors)
        {
            var current = LiteralQueryPlanner.Create(segment, scope.Ordinals, scope.Bytes, fragment.Text, features.ResolvedCaseMode, options);
            if (current.IsMissingGram)
            {
                candidates.Add(CreateRegexIndexedCandidate(in features, scope, current, estimatedCandidates: 0, options));
                return candidates;
            }

            fragmentPlans.Add(current);
        }

        var anchor = SelectAnchorPlan(fragmentPlans, features.ResolvedCaseMode, scope, features.RegexPlan);
        if (anchor.UsesIndex)
        {
            var selected = MergeFragmentGrams(segment, fragmentPlans, anchor, options);
            if (selected.Length > 0)
            {
                var estimatedCandidates = SearchPlanner.EstimateCandidateUpperBound(selected, scope.Ordinals.Length);
                var indexed = LiteralPlan.Indexed(
                    features.ResolvedCaseMode,
                    scope.Ordinals.Length,
                    scope.Bytes,
                    string.Join("|", features.Anchors.GetMandatoryTexts()),
                    selected,
                    anchor.IndexPair,
                    anchor.GramLength,
                    isMissing: false);
                candidates.Add(CreateRegexIndexedCandidate(in features, scope, indexed, estimatedCandidates, options));
            }
        }

        return candidates;
    }

    private static SearchPlanCandidate CreateLiteralContentPlan(in SearchQueryFeatures features, in LiteralPlan literalPlan, SiftOptions options)
    {
        var selected = SearchPlanner.ToSelectedNgrams(literalPlan.SelectedGrams);
        var directScanCost = Math.Max(literalPlan.ScopeBytes, literalPlan.ScopeDocuments) * features.RegexPlan.DirectScanCostMultiplier;
        double? estimatedIndexedCost = literalPlan.UsesIndex
            ? literalPlan.IsMissingGram ? 0d : SearchPlanner.EstimateIndexedCost(features.RegexPlan.ExactLiteral?.Length ?? features.Query.Pattern.Length, SearchPlanner.EstimateCandidateUpperBound(literalPlan.SelectedGrams, literalPlan.ScopeDocuments), literalPlan.SelectedGrams, features.RegexPlan)
            : null;
        var estimatedCandidates = literalPlan.UsesIndex && literalPlan.SelectedGrams.Length > 0
            ? SearchPlanner.EstimateCandidateUpperBound(literalPlan.SelectedGrams, literalPlan.ScopeDocuments)
            : literalPlan.IsMissingGram ? 0 : literalPlan.ScopeDocuments;
        var estimatedCost = literalPlan.UsesIndex
            ? literalPlan.IsMissingGram
                ? 0d
                : SearchPlanner.ShouldPreferLiteralDirectScan(literalPlan.ScopeDocuments, literalPlan.ScopeBytes, Encoding.UTF8.GetByteCount(features.RegexPlan.ExactLiteral ?? features.Query.Pattern), literalPlan.SelectedGrams, options)
                    ? directScanCost + 1d
                    : estimatedIndexedCost ?? 0d
            : directScanCost;
        return new SearchPlanCandidate(
            literalPlan,
            SearchPlanner.GetPlanFamily(features.Query, selected, features.HasMandatoryFragments, features.RegexPlan, literalPlan.UsesIndex, literalPlan.UnionCandidates, literalPlan.UnionGroupLengths is not null),
            SearchPlanner.GetCandidateKind(features.Query, selected, features.RegexPlan, literalPlan.UsesIndex, literalPlan.UnionCandidates, literalPlan.UnionGroupLengths is not null),
            SearchPlanKinds.GetVerifierKind(features.Query, features.RegexPlan, literalPlan.UsesIndex),
            directScanCost,
            estimatedIndexedCost,
            estimatedCandidates,
            selected,
            estimatedCost);
    }

    private static SearchPlanCandidate CreateDirectRegexCandidate(in SearchQueryFeatures features, SegmentScope scope)
    {
        return new SearchPlanCandidate(
            LiteralPlan.Direct(features.Query.CaseMode, scope.Ordinals.Length, scope.Bytes),
            features.RegexPlan.GetDirectScanPlanFamily(features.HasMandatoryFragments),
            SearchCandidateKind.DirectScan,
            SearchPlanKinds.GetVerifierKind(features.Query, features.RegexPlan, usesIndex: false),
            SearchPlanner.EstimateDirectScanCost(scope, features.RegexPlan),
            null,
            scope.Ordinals.Length,
            [],
            SearchPlanner.EstimateDirectScanCost(scope, features.RegexPlan));
    }

    private static SearchPlanCandidate CreateRegexIndexedCandidate(in SearchQueryFeatures features, SegmentScope scope, in LiteralPlan literalPlan, double estimatedCandidates, SiftOptions options)
    {
        var selected = SearchPlanner.ToSelectedNgrams(literalPlan.SelectedGrams);
        var minimumPatternBytes = literalPlan.MandatoryFragment.Length;
        if (minimumPatternBytes == 0 && selected.Length > 0)
        {
            minimumPatternBytes = selected[0].Length;
        }

        var directScanCost = Math.Max(literalPlan.ScopeBytes, literalPlan.ScopeDocuments) * features.RegexPlan.DirectScanCostMultiplier;
        double? estimatedIndexedCost = literalPlan.IsMissingGram
            ? 0d
            : SearchPlanner.EstimateIndexedCost(minimumPatternBytes, estimatedCandidates, literalPlan.SelectedGrams, features.RegexPlan);
        var estimatedCost = literalPlan.IsMissingGram
            ? 0d
            : SearchPlanner.ShouldPreferRegexDirectScan(scope, literalPlan.SelectedGrams, estimatedCandidates, directScanCost, estimatedIndexedCost ?? 0d, options)
                ? directScanCost + 1d
                : estimatedIndexedCost ?? 0d;
        return new SearchPlanCandidate(
            literalPlan,
            SearchPlanner.GetPlanFamily(features.Query, selected, features.HasMandatoryFragments, features.RegexPlan, usesIndex: true, literalPlan.UnionCandidates, literalPlan.UnionGroupLengths is not null),
            SearchPlanner.GetCandidateKind(features.Query, selected, features.RegexPlan, usesIndex: true, literalPlan.UnionCandidates, literalPlan.UnionGroupLengths is not null),
            SearchPlanKinds.GetVerifierKind(features.Query, features.RegexPlan, usesIndex: true),
            directScanCost,
            estimatedIndexedCost,
            estimatedCandidates,
            selected,
            estimatedCost);
    }

    private static LiteralPlan SelectAnchorPlan(List<LiteralPlan> fragmentPlans, CaseMode resolvedCaseMode, SegmentScope scope, in RegexExecutionPlan regexPlan)
    {
        var best = LiteralPlan.Direct(resolvedCaseMode, scope.Ordinals.Length, scope.Bytes);
        var bestScore = double.PositiveInfinity;
        foreach (var plan in fragmentPlans)
        {
            if (!plan.UsesIndex)
            {
                continue;
            }

            var score = SearchPlanner.EstimateIndexedCost(plan.MandatoryFragment.Length, SearchPlanner.EstimateCandidateUpperBound(plan.SelectedGrams, scope.Ordinals.Length), plan.SelectedGrams, regexPlan);
            if (score < bestScore)
            {
                best = plan;
                bestScore = score;
            }
        }

        return best;
    }

    private static (LiteralPlan Plan, double EstimatedCandidates)? TryCreateStructuredLeadingLiteralPlan(MappedSegment segment, SegmentScope scope, CaseMode resolvedCaseMode, string[] mandatoryFragments, in RegexExecutionPlan regexPlan, SiftOptions options)
    {
        var leadingLiteralSet = regexPlan.Utf8RegexProgram?.LeadingLiteralSet;
        if (leadingLiteralSet is null || leadingLiteralSet.Length < 2)
        {
            return null;
        }

        var sharedFragments = new List<LiteralPlan>(mandatoryFragments.Length);
        for (var i = 0; i < mandatoryFragments.Length; i++)
        {
            var fragmentPlan = SelectIndexedLiteralCandidate(segment, scope, mandatoryFragments[i], resolvedCaseMode, options);
            if (fragmentPlan.HasValue && !fragmentPlan.Value.IsMissingGram && fragmentPlan.Value.SelectedGrams.Length > 0)
            {
                sharedFragments.Add(fragmentPlan.Value);
            }
        }

        var selected = new List<SelectedGram>(Math.Min(leadingLiteralSet.Length * 2, options.MaxSelectedNgrams * Math.Max(1, leadingLiteralSet.Length)));
        var groupLengths = new List<int>(leadingLiteralSet.Length);
        IndexRegionPair? pair = null;
        var gramLength = 0;
        double estimatedCandidates = 0d;
        for (var i = 0; i < leadingLiteralSet.Length; i++)
        {
            var literal = Encoding.UTF8.GetString(leadingLiteralSet[i]);
            var literalPlan = SelectIndexedLiteralCandidate(segment, scope, literal, resolvedCaseMode, options);
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

            var branch = new List<SelectedGram>(indexed.SelectedGrams.Length + sharedFragments.Count);
            AddBranchGrams(branch, indexed.SelectedGrams);
            for (var fragmentIndex = 0; fragmentIndex < sharedFragments.Count; fragmentIndex++)
            {
                if (sharedFragments[fragmentIndex].IndexPair.Equals(pair.Value))
                {
                    AddBranchGrams(branch, sharedFragments[fragmentIndex].SelectedGrams);
                }
            }

            if (branch.Count == 0)
            {
                return null;
            }

            branch.Sort(static (left, right) => left.DocFreq.CompareTo(right.DocFreq));
            estimatedCandidates += SearchPlanner.EstimateCandidateUpperBound(CollectionsMarshal.AsSpan(branch), scope.Ordinals.Length);
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
            LiteralPlan.Indexed(
                resolvedCaseMode,
                scope.Ordinals.Length,
                scope.Bytes,
                string.Join("|", mandatoryFragments),
                [.. selected],
                pair.Value,
                gramLength,
                isMissing: false,
                unionCandidates: true,
                unionGroupLengths: [.. groupLengths]),
            Math.Clamp(estimatedCandidates, 1d, scope.Ordinals.Length));
    }

    private static LiteralPlan? TryCreateLeadingLiteralSetPlan(MappedSegment segment, SegmentScope scope, CaseMode resolvedCaseMode, in RegexExecutionPlan regexPlan, SiftOptions options)
    {
        var leadingLiteralSet = regexPlan.Utf8RegexProgram?.LeadingLiteralSet;
        if (leadingLiteralSet is null || leadingLiteralSet.Length == 0)
        {
            return null;
        }

        var selected = new List<SelectedGram>(Math.Min(leadingLiteralSet.Length, options.MaxSelectedNgrams));
        IndexRegionPair? pair = null;
        var gramLength = 0;
        for (var i = 0; i < leadingLiteralSet.Length && selected.Count < options.MaxSelectedNgrams; i++)
        {
            var literal = Encoding.UTF8.GetString(leadingLiteralSet[i]);
            var literalPlan = SelectIndexedLiteralCandidate(segment, scope, literal, resolvedCaseMode, options);
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
        return LiteralPlan.Indexed(resolvedCaseMode, scope.Ordinals.Length, scope.Bytes, string.Empty, [.. selected], pair.Value, gramLength, isMissing: false, unionCandidates: true);
    }

    private static LiteralPlan? TryCreateLiteralSetPlan(MappedSegment segment, SegmentScope scope, CaseMode resolvedCaseMode, string[] literals, SiftOptions options, bool isAdvisory)
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
            if (string.IsNullOrEmpty(literals[i]) || literals[i].Length < 3 || IsPoisonousSeedText(literals[i]))
            {
                continue;
            }

            var literalPlan = SelectIndexedLiteralCandidate(segment, scope, literals[i], resolvedCaseMode, options);
            if (!literalPlan.HasValue || literalPlan.Value.SelectedGrams.Length == 0)
            {
                continue;
            }

            var indexed = literalPlan.Value;
            if (isAdvisory && IsPoisonousSeedPlan(indexed, scope.Ordinals.Length, options))
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
            return LiteralPlan.Indexed(resolvedCaseMode, scope.Ordinals.Length, scope.Bytes, string.Empty, [], default, 3, isMissing: true, unionCandidates: true);
        }

        selected.Sort(static (left, right) => left.DocFreq.CompareTo(right.DocFreq));
        return LiteralPlan.Indexed(resolvedCaseMode, scope.Ordinals.Length, scope.Bytes, string.Empty, [.. selected], pair.Value, gramLength, isMissing: false, unionCandidates: true);
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

    private static void AddBranchGrams(List<SelectedGram> branch, ReadOnlySpan<SelectedGram> grams)
    {
        for (var i = 0; i < grams.Length; i++)
        {
            TryAddSelectedGram(branch, grams[i]);
        }
    }

    private static LiteralPlan? SelectIndexedLiteralCandidate(MappedSegment segment, SegmentScope scope, string literal, CaseMode resolvedCaseMode, SiftOptions options)
    {
        var candidates = LiteralQueryPlanner.Enumerate(segment, scope.Ordinals, scope.Bytes, literal, resolvedCaseMode, options);
        for (var i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].UsesIndex)
            {
                return candidates[i];
            }
        }

        return null;
    }

    private static SelectedGram[] MergeFragmentGrams(MappedSegment segment, List<LiteralPlan> fragmentPlans, LiteralPlan anchor, SiftOptions options)
    {
        var selected = new List<SelectedGram>(options.MaxSelectedNgrams);
        foreach (var plan in fragmentPlans)
        {
            if (!plan.UsesIndex || plan.SelectedGrams.Length == 0 || !plan.IndexPair.Equals(anchor.IndexPair))
            {
                continue;
            }

            AddMandatoryFragmentGrams(segment, plan, selected, options.MaxSelectedNgrams);
        }

        if (selected.Count == 0)
        {
            return [];
        }

        var targetCandidates = SearchPlanner.GetTargetCandidateDocuments(anchor.ScopeDocuments, options);
        if (targetCandidates == int.MaxValue || SearchPlanner.EstimateCandidateUpperBound(CollectionsMarshal.AsSpan(selected), anchor.ScopeDocuments) <= targetCandidates)
        {
            selected.Sort(static (left, right) => left.DocFreq.CompareTo(right.DocFreq));
            return [.. selected];
        }

        selected.Sort(static (left, right) => left.DocFreq.CompareTo(right.DocFreq));
        return [.. selected];
    }

    private static void AddMandatoryFragmentGrams(MappedSegment segment, LiteralPlan plan, List<SelectedGram> selected, int maxSelectedNgrams)
    {
        if (string.IsNullOrEmpty(plan.MandatoryFragment) || plan.GramLength <= 0)
        {
            return;
        }

        var caseInsensitiveAscii = IsCaseInsensitiveAsciiIndex(plan.IndexPair);
        var patternBytes = Encoding.UTF8.GetBytes(plan.MandatoryFragment);
        if (patternBytes.Length < plan.GramLength)
        {
            return;
        }

        for (var i = 0; i <= patternBytes.Length - plan.GramLength && selected.Count < maxSelectedNgrams; i++)
        {
            if (!TryEncodeGram(patternBytes.AsSpan(i, plan.GramLength), caseInsensitiveAscii, out var gram))
            {
                return;
            }

            if (!segment.TryGetPostings(plan.IndexPair, gram, out var postings, out var docFreq))
            {
                return;
            }

            TryAddSelectedGram(selected, new SelectedGram(gram, plan.GramLength, docFreq, postings.Length));
        }
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

    private static bool IsCaseInsensitiveAsciiIndex(IndexRegionPair pair)
    {
        return pair.Lexicon is SegmentRegionId.ContentTriIcLex or SegmentRegionId.ContentBiIcLex;
    }

    private static bool TryEncodeGram(ReadOnlySpan<byte> value, bool caseInsensitive, out int gram)
    {
        gram = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (caseInsensitive)
            {
                if (current > 0x7F)
                {
                    gram = 0;
                    return false;
                }

                current = (byte)(current is >= (byte)'A' and <= (byte)'Z' ? current + 32 : current);
            }

            gram = (gram << 8) | current;
        }

        return true;
    }
}

