using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;

namespace Lokad.Sift.Search;

// Provider failures must not be mistaken for damaged index packs by the
// snapshot's storage-error boundary. Public entry points rethrow the original.
internal sealed class RegexEngineFailureException(Exception exception) : Exception("The supplied regex engine failed.", exception)
{
    public ExceptionDispatchInfo DispatchInfo { get; } = ExceptionDispatchInfo.Capture(exception);
}

internal static class RegexEngineGuards
{
    public static bool TryCompile(ISiftRegexEngine engine, string pattern, RegexOptions options,
        TimeSpan timeout, Action checkpoint, [NotNullWhen(true)] out ISiftCompiledRegex? compiled)
    {
        try { return engine.TryCompile(pattern, options, timeout, checkpoint, out compiled); }
        catch (Exception exception) when (IndexCorruption.IsIndexCorruption(exception))
        {
            throw new RegexEngineFailureException(exception);
        }
    }

    public static SiftRegexInputDomain GetInputDomain(ISiftCompiledRegex compiled)
    {
        try { return compiled.InputDomain; }
        catch (Exception exception) when (IndexCorruption.IsIndexCorruption(exception))
        {
            throw new RegexEngineFailureException(exception);
        }
    }

    public static void Enumerate(ISiftCompiledRegex compiled, ReadOnlySpan<byte> input, Utf8RegexHitSink sink)
    {
        try { compiled.EnumerateMatches(input, sink, sink.Checkpoint); }
        catch (Exception exception) when (IndexCorruption.IsIndexCorruption(exception) &&
            !ReferenceEquals(exception, sink.StorageFailure))
        {
            throw new RegexEngineFailureException(exception);
        }
    }
}
