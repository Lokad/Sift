using Lokad.Sift;

namespace Lokad.Sift.Search;

internal readonly record struct SearchOrderedAnchor(
    byte[] Utf8Bytes,
    bool IgnoreCase,
    int? MaxGapBytesFromPrevious,
    bool SameLineFromPrevious);

internal readonly record struct SearchOrderedAnchorGroup(SearchOrderedAnchor[] Anchors);

internal readonly record struct SearchQueryAnchors(
    string[] MandatoryTexts,
    string[] AdvisoryTexts,
    CaseMode AdvisoryCaseMode,
    SearchOrderedAnchorGroup[] OrderedAnchorGroups)
{
    public bool HasMandatoryTexts => MandatoryTexts.Length > 0;

    public static SearchQueryAnchors Create(
        SearchQuery query,
        CaseMode resolvedCaseMode,
        in RegexExecutionPlan regexPlan,
        in RegexFragmentAnalyzer.PatternAnalysis analysis)
    {
        if (query.PatternMode == PatternMode.Regex &&
            resolvedCaseMode == CaseMode.Insensitive &&
            !regexPlan.HasByteAnalysis)
        {
            // A mandatory byte fragment is not mandatory under invariant Unicode
            // regex folding unless the pattern has byte-safe semantics for ASCII documents.
            return new SearchQueryAnchors([], [], CaseMode.Sensitive, []);
        }

        var mandatoryFragments = GetMandatoryFragments(query, regexPlan.ExactLiteral, analysis);
        var advisorySeeds = GetAdvisorySeeds(
            query,
            regexPlan.ExactLiteral,
            mandatoryFragments,
            analysis,
            out var advisoryCaseMode);
        var utf8RegexProgram = regexPlan.Utf8RegexProgram;
        var regexGroups = utf8RegexProgram?.OrderedAnchorGroups;
        if (utf8RegexProgram is null || regexGroups is null || regexGroups.Length == 0)
        {
            return new SearchQueryAnchors(mandatoryFragments, advisorySeeds, advisoryCaseMode, []);
        }

        var orderedAnchorGroups = new SearchOrderedAnchorGroup[regexGroups.Length];
        var ignoreCase = utf8RegexProgram.IgnoreCase;
        for (var groupIndex = 0; groupIndex < regexGroups.Length; groupIndex++)
        {
            var regexAnchors = regexGroups[groupIndex].Anchors;
            var anchors = new SearchOrderedAnchor[regexAnchors.Length];
            for (var anchorIndex = 0; anchorIndex < regexAnchors.Length; anchorIndex++)
            {
                var anchor = regexAnchors[anchorIndex];
                anchors[anchorIndex] = new SearchOrderedAnchor(
                    anchor.Utf8Bytes,
                    ignoreCase,
                    anchor.MaxGapBytesFromPrevious,
                    anchor.SameLineFromPrevious);
            }

            orderedAnchorGroups[groupIndex] = new SearchOrderedAnchorGroup(anchors);
        }

        return new SearchQueryAnchors(mandatoryFragments, advisorySeeds, advisoryCaseMode, orderedAnchorGroups);

        static string[] GetMandatoryFragments(
            SearchQuery query,
            string? exactRegexLiteral,
            in RegexFragmentAnalyzer.PatternAnalysis analysis)
        {
            if (query.PatternMode == PatternMode.Regex)
            {
                return exactRegexLiteral is not null
                    ? [exactRegexLiteral]
                    : LiteralFragmentSet.PruneContained(analysis.MandatoryFragments);
            }

            return query.Pattern.Length == 0 ? [] : [query.Pattern];
        }

        static string[] GetAdvisorySeeds(
            SearchQuery query,
            string? exactRegexLiteral,
            string[] mandatoryFragments,
            in RegexFragmentAnalyzer.PatternAnalysis analysis,
            out CaseMode advisoryCaseMode)
        {
            advisoryCaseMode = query.CaseMode == CaseMode.Insensitive
                ? CaseMode.Insensitive
                : CaseMode.Sensitive;

            if (query.PatternMode != PatternMode.Regex || exactRegexLiteral is not null)
            {
                return [];
            }

            var advisoryPlan = analysis.AdvisorySeeds;
            advisoryCaseMode = advisoryPlan.RequiresIgnoreCase ? CaseMode.Insensitive : advisoryCaseMode;
            var seeds = advisoryPlan.Seeds;
            if (seeds.Length == 0)
            {
                return [];
            }

            var filtered = new List<string>(seeds.Length);
            for (var i = 0; i < seeds.Length; i++)
            {
                var seed = seeds[i];
                if (seed.Length < 3)
                {
                    continue;
                }

                var redundant = false;
                for (var j = 0; j < mandatoryFragments.Length; j++)
                {
                    if (mandatoryFragments[j].Contains(seed, StringComparison.Ordinal))
                    {
                        redundant = true;
                        break;
                    }
                }

                if (!redundant)
                {
                    filtered.Add(seed);
                }
            }

            return [.. filtered];
        }
    }
}
