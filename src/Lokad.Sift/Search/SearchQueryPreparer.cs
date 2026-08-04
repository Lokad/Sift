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

            var regexPlan = RegexExecutionPlanner.Resolve(query, resolvedCaseMode, analysis, utf8Syntax);
            var anchors = BuildQueryAnchors(query, resolvedCaseMode, regexPlan, analysis);
            return new PreparedSearchQuery(
                resolvedCaseMode,
                regexPlan,
                anchors,
                anchors.MandatoryTexts,
                anchors.AdvisoryTexts,
                PrepareLiteralQuery(query, resolvedCaseMode, regexPlan.ExactLiteral),
                PrepareMandatoryFragments(anchors.MandatoryTexts, resolvedCaseMode),
                PrepareOrderedAnchorGroups(anchors.OrderedAnchorGroups),
                () => PrepareRegexExecution(query, resolvedCaseMode, regexPlan));
        }

        static PreparedLiteralQuery? PrepareLiteralQuery(
        SearchQuery query,
        CaseMode resolvedCaseMode,
        string? literalOverride)
        {
            var pattern = literalOverride ?? query.Pattern;
            if ((query.PatternMode != PatternMode.Literal && literalOverride is null) || pattern.Length == 0)
            {
                return null;
            }

            var needle = Encoding.UTF8.GetBytes(pattern);
            var isAscii = needle.AsSpan().IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0;
            var loweredAsciiNeedle = resolvedCaseMode == CaseMode.Insensitive && isAscii
                ? LowerAscii(needle)
                : null;
            return new PreparedLiteralQuery(
                needle,
                loweredAsciiNeedle,
                loweredAsciiNeedle is null ? null : BuildPrefixTable(loweredAsciiNeedle),
                isAscii);
        }

        static PreparedMandatoryFragment[] PrepareMandatoryFragments(
        string[] mandatoryTexts,
        CaseMode resolvedCaseMode)
        {
            if (mandatoryTexts.Length == 0)
            {
                return [];
            }

            var prepared = new PreparedMandatoryFragment[mandatoryTexts.Length];
            for (var i = 0; i < mandatoryTexts.Length; i++)
            {
                var text = mandatoryTexts[i];
                var utf8Bytes = Encoding.UTF8.GetBytes(text);
                var isAscii = utf8Bytes.AsSpan().IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0;
                var loweredAsciiBytes = resolvedCaseMode == CaseMode.Insensitive && isAscii
                    ? LowerAscii(utf8Bytes)
                    : [];
                prepared[i] = new PreparedMandatoryFragment(
                    text,
                    utf8Bytes,
                    loweredAsciiBytes,
                    BuildPrefixTable(loweredAsciiBytes),
                    isAscii);
            }

            return prepared;
        }

        static PreparedOrderedAnchor[][] PrepareOrderedAnchorGroups(SearchOrderedAnchorGroup[] groups)
        {
            if (groups.Length == 0)
            {
                return [];
            }

            var prepared = new PreparedOrderedAnchor[groups.Length][];
            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                var group = groups[groupIndex].Anchors;
                prepared[groupIndex] = new PreparedOrderedAnchor[group.Length];
                for (var anchorIndex = 0; anchorIndex < group.Length; anchorIndex++)
                {
                    var anchor = group[anchorIndex];
                    var lowered = anchor.IgnoreCase ? LowerAscii(anchor.Utf8Bytes) : null;
                    prepared[groupIndex][anchorIndex] = new PreparedOrderedAnchor(
                        anchor.Utf8Bytes,
                        lowered,
                        lowered is null ? [] : BuildPrefixTable(lowered),
                        anchor.MaxGapBytesFromPrevious,
                        anchor.SameLineFromPrevious);
                }
            }

            return prepared;
        }

        static PreparedRegexExecution PrepareRegexExecution(
        SearchQuery query,
        CaseMode resolvedCaseMode,
        in RegexExecutionPlan regexPlan)
        {
            if (query.PatternMode != PatternMode.Regex || regexPlan.IsExactLiteral)
            {
                return PreparedRegexExecution.None;
            }

            if (regexPlan.Utf8RegexProgram is { } utf8RegexProgram)
            {
                return new PreparedRegexExecution(
                    Utf8RegexEngine.Prepare(utf8RegexProgram, query.TimeoutPolicy),
                    () => RegexVerifier.Prepare(query, resolvedCaseMode));
            }

            return regexPlan.PreparedFallbackRegex is { } fallbackRegex
                ? new PreparedRegexExecution(fallbackRegex)
                : throw new InvalidOperationException("The regex execution plan has no fallback verifier.");
        }

        static SearchQueryAnchors BuildQueryAnchors(
        SearchQuery query,
        CaseMode resolvedCaseMode,
        in RegexExecutionPlan regexPlan,
        in RegexFragmentAnalyzer.PatternAnalysis analysis)
        {
            if (query.PatternMode == PatternMode.Regex &&
                resolvedCaseMode == CaseMode.Insensitive &&
                !regexPlan.UsesUtf8Regex)
            {
                // A mandatory byte fragment is not mandatory under invariant Unicode
                // regex folding unless the pattern has byte-safe semantics for ASCII documents.
                return new SearchQueryAnchors([], [], CaseMode.Sensitive, []);
            }

            var mandatoryFragments = GetMandatoryFragments(query, regexPlan.ExactLiteral, analysis);
            var advisorySeeds = GetAdvisorySeeds(query, regexPlan.ExactLiteral, mandatoryFragments, analysis, out var advisoryCaseMode);
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
                    anchors[anchorIndex] = new SearchOrderedAnchor(anchor.Utf8Bytes, ignoreCase, anchor.MaxGapBytesFromPrevious, anchor.SameLineFromPrevious);
                }

                orderedAnchorGroups[groupIndex] = new SearchOrderedAnchorGroup(anchors);
            }

            return new SearchQueryAnchors(mandatoryFragments, advisorySeeds, advisoryCaseMode, orderedAnchorGroups);
        }

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
            advisoryCaseMode = query.CaseMode switch
            {
                CaseMode.Insensitive => CaseMode.Insensitive,
                _ => CaseMode.Sensitive
            };

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
