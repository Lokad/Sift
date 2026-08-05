using System.Text;

namespace Lokad.Sift.Search;

internal readonly record struct PreparedQueryKey(
    string Pattern,
    PatternMode PatternMode,
    CaseMode CaseMode,
    bool WholeWord,
    bool WholeLine,
    bool Multiline,
    bool DotAll,
    TimeoutPolicy TimeoutPolicy)
{
    public SearchQuery ToSearchQuery()
    {
        return new SearchQuery(Pattern, PatternMode, CaseMode)
        {
            WholeWord = WholeWord,
            WholeLine = WholeLine,
            Multiline = Multiline,
            DotAll = DotAll,
            TimeoutPolicy = TimeoutPolicy
        };
    }
}

internal sealed class PreparedSearchQuery
{
    private readonly Lazy<PreparedRegexExecution> _regexExecution;
    private int _utf8RegexReturned;

    public PreparedSearchQuery(
        CaseMode resolvedCaseMode,
        RegexExecutionPlan regexPlan,
        SearchQueryAnchors anchors,
        PreparedLiteralQuery? literal,
        PreparedMandatoryFragment[] mandatoryFragmentsForPrecheck,
        PreparedOrderedAnchor[][] orderedAnchorGroups,
        Func<PreparedRegexExecution> prepareRegexExecution)
    {
        ResolvedCaseMode = resolvedCaseMode;
        RegexPlan = regexPlan;
        Anchors = anchors;
        Literal = literal;
        MandatoryFragmentsForPrecheck = mandatoryFragmentsForPrecheck;
        OrderedAnchorGroups = orderedAnchorGroups;
        _regexExecution = new Lazy<PreparedRegexExecution>(
            prepareRegexExecution,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public CaseMode ResolvedCaseMode { get; }
    public RegexExecutionPlan RegexPlan { get; }
    public SearchQueryAnchors Anchors { get; }
    public PreparedLiteralQuery? Literal { get; }
    public PreparedMandatoryFragment[] MandatoryFragmentsForPrecheck { get; }
    public PreparedOrderedAnchor[][] OrderedAnchorGroups { get; }

    public PreparedRegexExecutionAccess GetRegexExecution()
    {
        var execution = _regexExecution.Value;
        bool? utf8RegexCacheHit = execution.Utf8Regex is { } utf8Regex
            ? utf8Regex.CacheHit || Interlocked.Exchange(ref _utf8RegexReturned, 1) != 0
            : null;
        return new PreparedRegexExecutionAccess(execution, utf8RegexCacheHit);
    }
}

internal readonly record struct PreparedRegexExecutionAccess(
    PreparedRegexExecution Execution,
    bool? Utf8RegexCacheHit)
{
    public PreparedUtf8Regex? Utf8Regex => Execution.Utf8Regex;

    public PreparedRegexVerifier? GetFallbackRegex()
    {
        return Execution.GetFallbackRegex();
    }
}

internal sealed class PreparedLiteralQuery(
    byte[] needle,
    byte[]? loweredAsciiNeedle,
    int[]? asciiPrefixTable,
    bool isAscii)
{
    public byte[] Needle { get; } = needle;
    public byte[]? LoweredAsciiNeedle { get; } = loweredAsciiNeedle;
    public int[]? AsciiPrefixTable { get; } = asciiPrefixTable;
    public bool IsAscii { get; } = isAscii;

    public static PreparedLiteralQuery? Create(
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
            ? SearchQueryPreparer.LowerAscii(needle)
            : null;
        return new PreparedLiteralQuery(
            needle,
            loweredAsciiNeedle,
            loweredAsciiNeedle is null ? null : SearchQueryPreparer.BuildPrefixTable(loweredAsciiNeedle),
            isAscii);
    }
}

internal sealed class PreparedRegexExecution
{
    public static PreparedRegexExecution None { get; } = new(null, prepareFallbackRegex: null);

    private readonly Lazy<PreparedRegexVerifier>? _fallbackRegex;
    private int _fallbackReturned;

    public PreparedRegexExecution(
        PreparedUtf8Regex? utf8Regex,
        Func<PreparedRegexVerifier>? prepareFallbackRegex)
    {
        Utf8Regex = utf8Regex;
        _fallbackRegex = prepareFallbackRegex is null
            ? null
            : new Lazy<PreparedRegexVerifier>(
                prepareFallbackRegex,
                LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public PreparedRegexExecution(PreparedRegexVerifier fallbackRegex)
        : this(null, () => fallbackRegex)
    {
    }

    public PreparedUtf8Regex? Utf8Regex { get; }

    public PreparedRegexVerifier? GetFallbackRegex()
    {
        if (_fallbackRegex is null)
        {
            return null;
        }

        var fallback = _fallbackRegex.Value;
        if (Interlocked.Exchange(ref _fallbackReturned, 1) != 0)
        {
            RegexVerifier.RecordPreparedCacheHit();
        }

        return fallback;
    }

    public static PreparedRegexExecution Create(
        SearchQuery query,
        CaseMode resolvedCaseMode,
        in RegexExecutionPlan regexPlan)
    {
        if (query.PatternMode != PatternMode.Regex || regexPlan.IsExactLiteral)
        {
            return None;
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
}

internal sealed class PreparedMandatoryFragment(
    string text,
    byte[] utf8Bytes,
    byte[] loweredAsciiBytes,
    int[] asciiPrefixTable,
    bool isAscii)
{
    public string Text { get; } = text;
    public byte[] Utf8Bytes { get; } = utf8Bytes;
    public byte[] LoweredAsciiBytes { get; } = loweredAsciiBytes;
    public int[] AsciiPrefixTable { get; } = asciiPrefixTable;
    public bool IsAscii { get; } = isAscii;

    public static PreparedMandatoryFragment[] CreateMany(
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
                ? SearchQueryPreparer.LowerAscii(utf8Bytes)
                : [];
            prepared[i] = new PreparedMandatoryFragment(
                text,
                utf8Bytes,
                loweredAsciiBytes,
                SearchQueryPreparer.BuildPrefixTable(loweredAsciiBytes),
                isAscii);
        }

        return prepared;
    }
}

internal sealed class PreparedOrderedAnchor(
    byte[] utf8Bytes,
    byte[]? loweredAsciiBytes,
    int[] asciiPrefixTable,
    int? maxGapBytesFromPrevious,
    bool sameLineFromPrevious)
{
    public byte[] Utf8Bytes { get; } = utf8Bytes;
    public byte[]? LoweredAsciiBytes { get; } = loweredAsciiBytes;
    public int[] AsciiPrefixTable { get; } = asciiPrefixTable;
    public int? MaxGapBytesFromPrevious { get; } = maxGapBytesFromPrevious;
    public bool SameLineFromPrevious { get; } = sameLineFromPrevious;

    public static PreparedOrderedAnchor[][] CreateGroups(SearchOrderedAnchorGroup[] groups)
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
                var lowered = anchor.IgnoreCase ? SearchQueryPreparer.LowerAscii(anchor.Utf8Bytes) : null;
                prepared[groupIndex][anchorIndex] = new PreparedOrderedAnchor(
                    anchor.Utf8Bytes,
                    lowered,
                    lowered is null ? [] : SearchQueryPreparer.BuildPrefixTable(lowered),
                    anchor.MaxGapBytesFromPrevious,
                    anchor.SameLineFromPrevious);
            }
        }

        return prepared;
    }
}
