using Lokad.Sift;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Search;

internal readonly record struct SearchQueryAnchors(
    string[] MandatoryTexts,
    string[] AdvisoryTexts)
{
    public static SearchQueryAnchors Create(
        SearchQuery query,
        CaseMode resolvedCaseMode,
        in RegexExecutionPlan regexPlan,
        in RegexFragmentAnalyzer.PatternAnalysis analysis)
    {
        if (query.Pattern.Length == 0)
            return new SearchQueryAnchors([], []);

        if (query.PatternMode == PatternMode.Regex &&
            resolvedCaseMode == CaseMode.Insensitive &&
            !regexPlan.HasByteAnalysis)
        {
            // A mandatory byte fragment is not mandatory under invariant Unicode
            // regex folding unless the pattern has byte-safe semantics for ASCII documents.
            return new SearchQueryAnchors([], []);
        }

        var mandatoryFragments = GetMandatoryFragments(query, regexPlan.ExactLiteral, analysis);
        var advisorySeeds = GetAdvisorySeeds(
            query,
            regexPlan.ExactLiteral,
            mandatoryFragments,
            analysis);
        return new SearchQueryAnchors(mandatoryFragments, advisorySeeds);

        static string[] GetMandatoryFragments(
            SearchQuery query,
            string? exactRegexLiteral,
            in RegexFragmentAnalyzer.PatternAnalysis analysis)
        {
            if (query.PatternMode == PatternMode.Regex)
            {
                return exactRegexLiteral is not null
                    ? [exactRegexLiteral]
                    : analysis.MandatoryFragments;
            }

            return UnicodeText.IsWellFormedUtf16(query.Pattern) ? [query.Pattern] : [];
        }

        static string[] GetAdvisorySeeds(
            SearchQuery query,
            string? exactRegexLiteral,
            string[] mandatoryFragments,
            in RegexFragmentAnalyzer.PatternAnalysis analysis)
        {
            if (query.PatternMode != PatternMode.Regex || exactRegexLiteral is not null)
            {
                return [];
            }

            var seeds = analysis.AdvisorySeeds;
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
