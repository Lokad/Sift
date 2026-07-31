using System.Text;
using Lokad.Sift;

namespace Lokad.Sift.Search;

internal static class RegexExecutionPlanner
{
    public static RegexExecutionPlan Resolve(in SearchQuery query, CaseMode resolvedCaseMode)
    {
        if (query.PatternMode != PatternMode.Regex)
        {
            return RegexExecutionPlan.Literal;
        }

        // Invariant regex case folding is not equivalent to ordinal or ASCII folding.
        // Only sensitive regexes can therefore be replaced by the literal verifier.
        if (resolvedCaseMode == CaseMode.Sensitive &&
            RegexFragmentAnalyzer.TryExtractExactLiteral(query.Pattern, out var exactLiteral))
        {
            return RegexExecutionPlan.ForExactLiteral(exactLiteral);
        }

        if (Utf8RegexEngine.TryCompile(query, resolvedCaseMode, out var byteRegexProgram))
        {
            return RegexExecutionPlan.ForUtf8Regex(byteRegexProgram);
        }

        return RegexExecutionPlan.ForFallback(RegexVerifier.Prepare(query, resolvedCaseMode));
    }

    public static bool NeedsUnicodeDecode(in SearchQuery query, string[] mandatoryFragments, CaseMode resolvedCaseMode, in RegexExecutionPlan regexPlan)
    {
        if (query.PatternMode == PatternMode.Regex && !regexPlan.IsExactLiteral && !regexPlan.UsesUtf8Regex)
        {
            return true;
        }

        if (query.PatternMode == PatternMode.Regex && (regexPlan.IsExactLiteral || regexPlan.UsesUtf8Regex))
        {
            return false;
        }

        if (query.Multiline || query.DotAll)
        {
            return true;
        }

        var patternBytes = Encoding.UTF8.GetBytes(query.Pattern);
        var asciiPattern = patternBytes.AsSpan().IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0;
        if (query.WholeLine)
        {
            return !asciiPattern;
        }

        if (resolvedCaseMode == CaseMode.Sensitive)
        {
            return false;
        }

        for (var i = 0; i < mandatoryFragments.Length; i++)
        {
            if (Encoding.UTF8.GetBytes(mandatoryFragments[i]).AsSpan().IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public static SearchCandidateKind GetCandidateKind(in SearchQuery query, bool usesIndex, List<SelectedNGram> selected, in RegexExecutionPlan regexPlan)
    {
        if (!usesIndex)
        {
            return SearchCandidateKind.DirectScan;
        }

        if (selected.Count == 0)
        {
            return SearchCandidateKind.Indexed;
        }

        var selectedLength = selected.Count == 0 ? 0 : selected[0].Length;
        return SearchPlanKinds.GetCandidateKind(query, selectedLength, regexPlan, usesIndex, unionCandidates: false, branchUnion: false);
    }

    public static SearchPlanFamily GetPlanFamily(in SearchQuery query, bool usesIndex, List<SelectedNGram> selected, bool hasMandatoryFragments, in RegexExecutionPlan regexPlan)
    {
        var selectedLength = selected.Count == 0 ? 0 : selected[0].Length;
        return SearchPlanKinds.GetPlanFamily(query, selectedLength, hasMandatoryFragments, regexPlan, usesIndex, unionCandidates: false, branchUnion: false);
    }
}

internal enum RegexExecutionKind
{
    Literal,
    ExactLiteral,
    Byte,
    Fallback
}

internal readonly record struct RegexExecutionPlan(
    RegexExecutionKind Kind,
    string? ExactLiteral,
    Utf8RegexProgram? Utf8RegexProgram,
    PreparedRegexVerifier? PreparedFallbackRegex)
{
    public static RegexExecutionPlan Literal => new(RegexExecutionKind.Literal, null, null, null);

    public static RegexExecutionPlan ForExactLiteral(string literal) => new(RegexExecutionKind.ExactLiteral, literal, null, null);

    public static RegexExecutionPlan ForUtf8Regex(Utf8RegexProgram program) => new(RegexExecutionKind.Byte, null, program, null);

    public static RegexExecutionPlan ForFallback(PreparedRegexVerifier preparedRegex) => new(RegexExecutionKind.Fallback, null, null, preparedRegex);

    public bool CanUseNonBacktracking => PreparedFallbackRegex?.UsesNonBacktracking == true;

    public bool IsExactLiteral => Kind == RegexExecutionKind.ExactLiteral;

    public bool UsesUtf8Regex => Kind == RegexExecutionKind.Byte;

    public string PatternKind => Kind switch
    {
        RegexExecutionKind.ExactLiteral => "RegexLiteral",
        RegexExecutionKind.Byte => "Utf8Regex",
        RegexExecutionKind.Fallback => "Regex",
        _ => "Literal"
    };

    public double DirectScanCostMultiplier => Kind switch
    {
        RegexExecutionKind.Byte => 1.25d,
        RegexExecutionKind.Fallback => 3d,
        _ => 1d
    };

    public double VerifierCostMultiplier => Kind switch
    {
        RegexExecutionKind.Byte => 1.5d,
        RegexExecutionKind.Fallback => 4d,
        _ => 1d
    };

    public SearchPlanFamily GetDirectScanPlanFamily(bool hasMandatoryFragments) =>
        SearchPlanKinds.GetDirectScanPlanFamily(hasMandatoryFragments, this);
}
