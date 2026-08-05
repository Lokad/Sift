using Lokad.Sift;

namespace Lokad.Sift.Search;

internal enum RegexExecutionKind
{
    Literal,
    ExactLiteral,
    Utf8,
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

    public static RegexExecutionPlan ForUtf8Regex(Utf8RegexProgram program) => new(RegexExecutionKind.Utf8, null, program, null);

    public static RegexExecutionPlan ForFallback(PreparedRegexVerifier preparedRegex) => new(RegexExecutionKind.Fallback, null, null, preparedRegex);

    public bool CanUseNonBacktracking => PreparedFallbackRegex?.UsesNonBacktracking == true;

    public bool IsExactLiteral => Kind == RegexExecutionKind.ExactLiteral;

    public bool UsesUtf8Regex => Kind == RegexExecutionKind.Utf8;

    public double DirectScanCostMultiplier => Kind switch
    {
        RegexExecutionKind.Utf8 => 1.25d,
        RegexExecutionKind.Fallback => 3d,
        _ => 1d
    };

    public double VerifierCostMultiplier => Kind switch
    {
        RegexExecutionKind.Utf8 => 1.5d,
        RegexExecutionKind.Fallback => 4d,
        _ => 1d
    };
}
