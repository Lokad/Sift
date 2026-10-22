using System.Text;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Search;

internal readonly record struct PreparedQueryKey(
    string Pattern,
    PatternMode PatternMode,
    CaseMode CaseMode,
    bool WholeWord,
    bool WholeLine,
    bool Multiline,
    bool DotAll,
    RegexTimeoutPolicy RegexTimeoutPolicy)
{
    public static PreparedQueryKey FromQuery(SearchQuery query) => new(
        query.Pattern, query.PatternMode, query.CaseMode, query.WholeWord, query.WholeLine,
        query.Multiline, query.DotAll, query.RegexTimeoutPolicy);

    public SearchQuery ToSearchQuery()
    {
        return new SearchQuery(Pattern, PatternMode, CaseMode)
        {
            WholeWord = WholeWord,
            WholeLine = WholeLine,
            Multiline = Multiline,
            DotAll = DotAll,
            RegexTimeoutPolicy = RegexTimeoutPolicy
        };
    }
}

internal sealed class PreparedSearchQuery
{
    private readonly Lazy<PreparedRegexVerifier>? _fallbackRegex;

    public PreparedSearchQuery(
        CaseMode resolvedCaseMode,
        RegexExecutionPlan regexPlan,
        SearchQueryAnchors anchors,
        PreparedLiteralQuery? literal,
        PreparedSearchText[] mandatoryTexts,
        PreparedSearchText[] leadingLiterals,
        PreparedMandatoryFragment[] mandatoryFragmentsForPrecheck,
        Func<PreparedRegexVerifier>? prepareFallbackRegex)
    {
        ResolvedCaseMode = resolvedCaseMode;
        RegexPlan = regexPlan;
        Anchors = anchors;
        Literal = literal;
        MandatoryTexts = mandatoryTexts;
        LeadingLiterals = leadingLiterals;
        MandatoryFragmentsForPrecheck = mandatoryFragmentsForPrecheck;
        _fallbackRegex = prepareFallbackRegex is null
            ? null
            : new Lazy<PreparedRegexVerifier>(prepareFallbackRegex, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public CaseMode ResolvedCaseMode { get; }
    public RegexExecutionPlan RegexPlan { get; }
    public SearchQueryAnchors Anchors { get; }
    public PreparedLiteralQuery? Literal { get; }
    public PreparedSearchText[] MandatoryTexts { get; }
    public PreparedSearchText[] LeadingLiterals { get; }
    public PreparedMandatoryFragment[] MandatoryFragmentsForPrecheck { get; }

    internal void DisposeCompiledRegex() => RegexPlan.PreparedUtf8Regex?.Regex.Dispose();

    public PreparedRegexVerifier? GetFallbackRegex() => RegexPlan.PreparedFallbackRegex ?? _fallbackRegex?.Value;
}

internal sealed class PreparedLiteralQuery(
    string text,
    byte[]? needle,
    byte[]? loweredAsciiNeedle,
    int[]? asciiPrefixTable,
    bool isAscii)
{
    public string Text { get; } = text;
    // No byte representation is available for literals containing lone surrogates.
    public byte[]? Needle { get; } = needle;
    public byte[]? LoweredAsciiNeedle { get; } = loweredAsciiNeedle;
    public int[]? AsciiPrefixTable { get; } = asciiPrefixTable;
    public bool IsAscii { get; } = isAscii;

    public bool CanUseByteVerifier(SearchQuery query, bool asciiDocument) =>
        IsAscii && (!query.WholeWord || query.WholeLine || asciiDocument);

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

        var needle = UnicodeText.IsWellFormedUtf16(pattern) ? Encoding.UTF8.GetBytes(pattern) : null;
        var isAscii = needle is not null && needle.AsSpan().IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0;
        var loweredAsciiNeedle = resolvedCaseMode == CaseMode.Insensitive && isAscii
            ? SearchQueryPreparer.LowerAscii(needle)
            : null;
        return new PreparedLiteralQuery(
            pattern,
            needle,
            loweredAsciiNeedle,
            loweredAsciiNeedle is null ? null : SearchQueryPreparer.BuildPrefixTable(loweredAsciiNeedle),
            isAscii);
    }
}

internal sealed class PreparedSearchText(string text, byte[] utf8Bytes)
{
    public string Text { get; } = text;
    public byte[] Utf8Bytes { get; } = utf8Bytes;

    public static PreparedSearchText[] CreateMany(string[] values)
    {
        var prepared = new PreparedSearchText[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            prepared[i] = new PreparedSearchText(values[i], Encoding.UTF8.GetBytes(values[i]));
        }

        return prepared;
    }

    public static PreparedSearchText[] CreateMany(byte[][] values)
    {
        var prepared = new PreparedSearchText[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            prepared[i] = new PreparedSearchText(
                Encoding.UTF8.GetString(values[i]),
                values[i]);
        }

        return prepared;
    }
}

internal sealed class PreparedMandatoryFragment(
    byte[] utf8Bytes,
    byte[] loweredAsciiBytes,
    int[] asciiPrefixTable)
{
    public byte[] Utf8Bytes { get; } = utf8Bytes;
    public byte[] LoweredAsciiBytes { get; } = loweredAsciiBytes;
    public int[] AsciiPrefixTable { get; } = asciiPrefixTable;

    public static PreparedMandatoryFragment[] CreateMany(
        PreparedSearchText[] mandatoryTexts,
        CaseMode resolvedCaseMode)
    {
        if (mandatoryTexts.Length == 0)
        {
            return [];
        }

        var prepared = new PreparedMandatoryFragment[mandatoryTexts.Length];
        for (var i = 0; i < mandatoryTexts.Length; i++)
        {
            var utf8Bytes = mandatoryTexts[i].Utf8Bytes;
            // Insensitive regex prechecks are prepared only from byte-analyzed
            // ASCII fragments; other insensitive patterns have no byte anchors.
            var loweredAsciiBytes = resolvedCaseMode == CaseMode.Insensitive
                ? SearchQueryPreparer.LowerAscii(utf8Bytes)
                : [];
            prepared[i] = new PreparedMandatoryFragment(
                utf8Bytes,
                loweredAsciiBytes,
                resolvedCaseMode == CaseMode.Insensitive
                    ? SearchQueryPreparer.BuildPrefixTable(loweredAsciiBytes)
                    : []);
        }

        return prepared;
    }
}
