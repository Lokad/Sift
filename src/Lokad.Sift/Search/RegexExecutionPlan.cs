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
    PreparedRegexVerifier? PreparedFallbackRegex,
    PreparedUtf8Regex? PreparedUtf8Regex = null,
    SiftRegexRouteReason RouteReason = SiftRegexRouteReason.Literal)
{
    public static RegexExecutionPlan Literal => new(RegexExecutionKind.Literal, null, null, null);

    public static RegexExecutionPlan ForExactLiteral(string literal) => new(RegexExecutionKind.ExactLiteral, literal, null, null);

    public static RegexExecutionPlan ForUtf8Regex(Utf8RegexProgram program, PreparedUtf8Regex compiled) =>
        new(RegexExecutionKind.Utf8, null, program, null, compiled, SiftRegexRouteReason.Native);

    public static RegexExecutionPlan ForFallback(PreparedRegexVerifier preparedRegex,
        Utf8RegexProgram? analysis = null, SiftRegexRouteReason reason = SiftRegexRouteReason.UnsupportedPattern) =>
        new(RegexExecutionKind.Fallback, null, analysis, preparedRegex, null, reason);

    public bool CanUseNonBacktracking => PreparedFallbackRegex?.UsesNonBacktracking == true;

    public bool IsExactLiteral => Kind == RegexExecutionKind.ExactLiteral;

    public bool UsesUtf8Regex => Kind == RegexExecutionKind.Utf8;

    public bool HasByteAnalysis => Utf8RegexProgram is not null;

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
