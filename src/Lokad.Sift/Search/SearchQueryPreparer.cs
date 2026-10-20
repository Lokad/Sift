using System.Text;
using System.Text.RegularExpressions;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Search;

internal static class SearchQueryPreparer
{
    internal const int CacheCapacity = 256;
    internal const int CacheCharacterCapacity = 256 * 1024;
    private static readonly BoundedLruCache<PreparedQueryKey, QueryAnalysis> AnalysisCache = new(
        CacheCapacity,
        CacheCharacterCapacity,
        static key => key.Pattern.Length);

#if SIFT_INTERNAL_VERIFICATION
    internal static int CacheCount => AnalysisCache.Count;
    internal static int CacheCharacters => AnalysisCache.Weight;
#endif

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

    internal static PreparedSearchQuery Create(PreparedQueryKey key, ISiftRegexEngine? engine, Action checkpoint)
    {
        var query = key.ToSearchQuery();
        var analyzed = AnalysisCache.GetOrAdd(key, Analyze, out _);
        var resolvedCaseMode = analyzed.CaseMode;
        var regexPlan = ResolveRegexExecution(query, analyzed, engine, checkpoint);
        try
        {
            var anchors = SearchQueryAnchors.Create(query, resolvedCaseMode, regexPlan, analyzed.Fragments);
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
                regexPlan.UsesUtf8Regex ? () => RegexVerifier.Prepare(query, resolvedCaseMode) : null);
        }
        catch
        {
            regexPlan.PreparedUtf8Regex?.Regex.Dispose();
            throw;
        }
    }

    private static QueryAnalysis Analyze(PreparedQueryKey key)
    {
        var query = key.ToSearchQuery();
        var resolved = query.CaseMode == CaseMode.Smart
            ? (Hashing.ContainsUppercase(query.Pattern) ? CaseMode.Sensitive : CaseMode.Insensitive)
            : query.CaseMode;
        Utf8RegexProgram? program = null;
        RegexFragmentAnalyzer.PatternAnalysis fragments = default;
        if (query.PatternMode == PatternMode.Regex)
        {
            if (Utf8RegexAnalysis.TryParseSyntax(query, resolved, out var syntax))
            {
                fragments = RegexFragmentAnalyzer.AnalyzePattern(syntax);
                Utf8RegexAnalysis.TryCompile(query, fragments.IsExactLiteral, syntax, out program);
            }
            else fragments = RegexFragmentAnalyzer.AnalyzeFallbackPattern(query.Pattern);
        }
        return new QueryAnalysis(resolved, program, fragments);
    }

    private static RegexExecutionPlan ResolveRegexExecution(SearchQuery query, QueryAnalysis analysis,
        ISiftRegexEngine? engine, Action checkpoint)
    {
        if (query.PatternMode != PatternMode.Regex) return RegexExecutionPlan.Literal;
        // Invariant regex folding differs from ordinal/ASCII literal folding.
        if (analysis.CaseMode == CaseMode.Sensitive && analysis.Fragments.IsExactLiteral &&
            analysis.Fragments.ExactLiteral is { } literal)
            return RegexExecutionPlan.ForExactLiteral(literal);

        var reason = engine is null ? SiftRegexRouteReason.NoEngine : SiftRegexRouteReason.UnsupportedPattern;
        if (engine is not null && analysis.Program is { } program)
        {
            var options = RegexOptions.CultureInvariant |
                (program.Multiline ? RegexOptions.Multiline : RegexOptions.None) |
                (program.DotAll ? RegexOptions.Singleline : RegexOptions.None) |
                (program.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            ISiftCompiledRegex? compiled = null;
            try
            {
                checkpoint();
                var accepted = RegexEngineGuards.TryCompile(engine, program.Pattern, options, TimeSpan.FromSeconds(1), checkpoint, out compiled);
                checkpoint();
                if (accepted)
                {
                    if (compiled is null || !Enum.IsDefined(RegexEngineGuards.GetInputDomain(compiled)))
                        throw new InvalidOperationException("The supplied regex engine returned an invalid compiled pattern.");
                    return RegexExecutionPlan.ForUtf8Regex(program,
                        new PreparedUtf8Regex(compiled, query.RegexTimeoutPolicy));
                }
                if (compiled is not null)
                    throw new InvalidOperationException("A declining regex engine returned a compiled pattern.");
                reason = SiftRegexRouteReason.EngineDeclined;
            }
            catch
            {
                compiled?.Dispose();
                throw;
            }
        }
        return RegexExecutionPlan.ForFallback(RegexVerifier.Prepare(query, analysis.CaseMode), analysis.Program, reason);
    }

    private sealed record QueryAnalysis(CaseMode CaseMode, Utf8RegexProgram? Program,
        RegexFragmentAnalyzer.PatternAnalysis Fragments);
}
