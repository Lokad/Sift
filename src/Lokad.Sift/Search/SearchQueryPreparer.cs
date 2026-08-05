using System.Text;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Search;

internal static class SearchQueryPreparer
{
    internal const int CacheCapacity = 256;
    internal const int CacheCharacterCapacity = 256 * 1024;
    private static readonly BoundedLruCache<PreparedQueryKey, PreparedSearchQuery> PreparedQueryCache = new(
        CacheCapacity,
        CacheCharacterCapacity,
        static key => key.Pattern.Length);

    internal static int CacheCount => PreparedQueryCache.Count;
    internal static int CacheCharacters => PreparedQueryCache.Weight;

    internal static int[] BuildPrefixTable(ReadOnlySpan<byte> needle)
    {
        var prefixTable = new int[needle.Length];
        var matched = 0;
        for (var i = 1; i < needle.Length; i++)
        {
            while (matched > 0 && needle[i] != needle[matched])
            {
                matched = prefixTable[matched - 1];
            }

            if (needle[i] == needle[matched])
            {
                matched++;
            }

            prefixTable[i] = matched;
        }

        return prefixTable;
    }

    internal static byte[] LowerAscii(ReadOnlySpan<byte> needle)
    {
        var loweredNeedle = GC.AllocateUninitializedArray<byte>(needle.Length);
        for (var i = 0; i < needle.Length; i++)
        {
            loweredNeedle[i] = AsciiCase.Fold(needle[i]);
        }

        return loweredNeedle;
    }

    internal static PreparedSearchQuery Prepare(SearchQuery query)
    {
        var key = new PreparedQueryKey(
            query.Pattern,
            query.PatternMode,
            query.CaseMode,
            query.WholeWord,
            query.WholeLine,
            query.Multiline,
            query.DotAll,
            query.TimeoutPolicy);
        return PreparedQueryCache.GetOrAdd(
            key,
            CreatePreparedSearchQuery,
            out _);

        static PreparedSearchQuery CreatePreparedSearchQuery(PreparedQueryKey key)
        {
            var query = key.ToSearchQuery();
            var resolvedCaseMode = ResolveCaseMode(query);
            Utf8RegexSyntax? utf8Syntax = null;
            RegexFragmentAnalyzer.PatternAnalysis analysis = default;
            if (query.PatternMode == PatternMode.Regex)
            {
                if (Utf8RegexEngine.TryParseSyntax(query, resolvedCaseMode, out var parsedSyntax))
                {
                    utf8Syntax = parsedSyntax;
                    analysis = RegexFragmentAnalyzer.AnalyzePattern(parsedSyntax);
                }
                else
                {
                    analysis = RegexFragmentAnalyzer.AnalyzeFallbackPattern(query.Pattern);
                }
            }

            var regexPlan = ResolveRegexExecution(query, resolvedCaseMode, analysis, utf8Syntax);
            var anchors = SearchQueryAnchors.Create(query, resolvedCaseMode, regexPlan, analysis);
            var mandatoryTexts = PreparedSearchText.CreateMany(anchors.MandatoryTexts);
            var advisoryTexts = PreparedSearchText.CreateMany(anchors.AdvisoryTexts);
            var usesLiteralVerifier = query.PatternMode == PatternMode.Literal || regexPlan.IsExactLiteral;
            return new PreparedSearchQuery(
                resolvedCaseMode,
                regexPlan,
                anchors,
                PreparedLiteralQuery.Create(query, resolvedCaseMode, regexPlan.ExactLiteral),
                new PreparedSearchText(query.Pattern, Encoding.UTF8.GetBytes(query.Pattern)),
                mandatoryTexts,
                advisoryTexts,
                PreparedSearchText.CreateMany(
                    regexPlan.Utf8RegexProgram?.LeadingLiteralSet ?? []),
                usesLiteralVerifier
                    ? []
                    : PreparedMandatoryFragment.CreateMany(mandatoryTexts, resolvedCaseMode),
                PreparedOrderedAnchor.CreateGroups(anchors.OrderedAnchorGroups),
                () => PreparedRegexExecution.Create(query, resolvedCaseMode, regexPlan));

            static RegexExecutionPlan ResolveRegexExecution(
                SearchQuery query,
                CaseMode resolvedCaseMode,
                in RegexFragmentAnalyzer.PatternAnalysis analysis,
                Utf8RegexSyntax? utf8Syntax)
            {
                if (query.PatternMode != PatternMode.Regex)
                {
                    return RegexExecutionPlan.Literal;
                }

                // Invariant regex case folding is not equivalent to ordinal or ASCII folding.
                // Only sensitive regexes can therefore be replaced by the literal verifier.
                if (resolvedCaseMode == CaseMode.Sensitive &&
                    analysis.IsExactLiteral &&
                    analysis.ExactLiteral is { } exactLiteral)
                {
                    return RegexExecutionPlan.ForExactLiteral(exactLiteral);
                }

                if (utf8Syntax is not null &&
                    Utf8RegexEngine.TryCompile(query, analysis.IsExactLiteral, utf8Syntax, out var utf8RegexProgram))
                {
                    return RegexExecutionPlan.ForUtf8Regex(utf8RegexProgram);
                }

                return RegexExecutionPlan.ForFallback(RegexVerifier.Prepare(query, resolvedCaseMode));
            }
        }

        static CaseMode ResolveCaseMode(SearchQuery query)
        {
            return query.CaseMode switch
            {
                CaseMode.Sensitive => CaseMode.Sensitive,
                CaseMode.Insensitive => CaseMode.Insensitive,
                CaseMode.Smart => Hashing.ContainsUppercase(query.Pattern) ? CaseMode.Sensitive : CaseMode.Insensitive,
                _ => CaseMode.Sensitive
            };
        }

    }
}
