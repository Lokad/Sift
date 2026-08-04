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

    public PreparedRegexExecution GetRegexExecution()
    {
        var wasPrepared = _regexExecution.IsValueCreated;
        var execution = _regexExecution.Value;
        if (wasPrepared && execution.FallbackRegex is not null)
        {
            RegexVerifier.RecordPreparedCacheHit();
        }

        if (!wasPrepared || execution.Utf8Regex is not { CacheHit: false } utf8Regex)
        {
            return execution;
        }

        return execution with { Utf8Regex = utf8Regex with { CacheHit = true } };
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

internal readonly record struct PreparedRegexExecution(
    PreparedUtf8Regex? Utf8Regex,
    PreparedRegexVerifier? FallbackRegex);

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
    int[]? asciiPrefixTable,
    int? maxGapBytesFromPrevious,
    bool sameLineFromPrevious)
{
    public byte[] Utf8Bytes { get; } = utf8Bytes;
    public byte[]? LoweredAsciiBytes { get; } = loweredAsciiBytes;
    public int[]? AsciiPrefixTable { get; } = asciiPrefixTable;
    public int? MaxGapBytesFromPrevious { get; } = maxGapBytesFromPrevious;
    public bool SameLineFromPrevious { get; } = sameLineFromPrevious;
}
