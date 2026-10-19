using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Lokad.Sift;

/// <summary>The input domain for which a compiled UTF-8 verifier guarantees .NET semantics.</summary>
public enum SiftRegexInputDomain
{
    Ascii,
    Utf8
}

/// <summary>Compiles patterns for byte-oriented verification without an internal .NET fallback.</summary>
/// <remarks>The application owns the engine and must keep its semantics stable while indexes
/// and snapshots use it. Compilation may be concurrent. Return false only for unsupported
/// native capabilities; cancellation, timeout and resource failures must throw.</remarks>
public interface ISiftRegexEngine
{
    /// <summary>Compiles an effective .NET pattern, or declines native execution.</summary>
    /// <remarks>Each success transfers an independently disposable ownership handle to Sift.
    /// Do not retain the checkpoint. Success guarantees
    /// byte-oriented execution and .NET match semantics on the handle's declared input domain.</remarks>
    bool TryCompile(string pattern, RegexOptions options, TimeSpan matchTimeout,
        Action checkpoint, [NotNullWhen(true)] out ISiftCompiledRegex? compiledPattern);
}

/// <summary>A Sift-owned, concurrently reusable UTF-8 pattern with independent per-call matching state.</summary>
/// <remarks>Honor the compilation timeout and throw RegexMatchTimeoutException on expiry.
/// Disposal must be idempotent and non-throwing. Sift disposes only after active users finish.</remarks>
public interface ISiftCompiledRegex : IDisposable
{
    /// <summary>Gets the input domain for which the compiled pattern preserves .NET semantics.</summary>
    SiftRegexInputDomain InputDomain { get; }

    /// <summary>Streams non-overlapping whole matches over the complete borrowed input.</summary>
    /// <remarks>Report absolute UTF-8 byte ranges in .NET order, including empty-match progression.
    /// Stop immediately when OnMatch returns false. Do not retain input, sink or checkpoint.
    /// Invoke the checkpoint during matching, including work that produces no matches.</remarks>
    void EnumerateMatches(ReadOnlySpan<byte> input, ISiftRegexMatchSink sink, Action checkpoint);
}

/// <summary>Consumes whole-match byte ranges synchronously; false stops enumeration.</summary>
public interface ISiftRegexMatchSink
{
    bool OnMatch(int byteOffset, int byteLength);
}

/// <summary>Why a content query selected its regex verifier.</summary>
public enum SiftRegexRouteReason
{
    Literal,
    NoEngine,
    UnsupportedPattern,
    EngineDeclined,
    Native,
    InputOutsideDomain,
    MixedInputDomains
}
