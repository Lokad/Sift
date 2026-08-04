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
        string[] mandatoryFragments,
        string[] advisorySeeds,
        PreparedLiteralQuery? literal,
        PreparedMandatoryFragment[] mandatoryFragmentsForPrecheck,
        PreparedOrderedAnchor[][] orderedAnchorGroups,
        Func<PreparedRegexExecution> prepareRegexExecution)
    {
        ResolvedCaseMode = resolvedCaseMode;
        RegexPlan = regexPlan;
        Anchors = anchors;
        MandatoryFragments = mandatoryFragments;
        AdvisorySeeds = advisorySeeds;
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
    public string[] MandatoryFragments { get; }
    public string[] AdvisorySeeds { get; }
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
}
