using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Lokad.Sift.Tests;

// A controlled provider for contract/lifetime tests. Its default whole-input range
// is used only with fixtures whose pattern matches that complete ASCII input.
internal sealed class RegexContractTestEngine : ISiftRegexEngine, IDisposable
{
    public int Compilations;
    public bool Decline;
    public bool NoMatches;
    public bool TimeoutCompile;
    public bool TimeoutMatch;
    public bool FailCompile;
    public bool InvalidRange;
    public bool ContinueAfterStop;
    public bool ReturnNullOnSuccess;
    public bool ReturnHandleOnDecline;
    public bool InvalidDomain;
    public bool TimeoutAfterMatch;
    public bool Disposed;
    public Action? BeforeCompile;
    public Action? BeforeMatch;
    public Action? AfterCompile;
    public (int Start, int Length)[]? Ranges;
    public ConcurrentBag<Pattern> Patterns { get; } = [];
    public ConcurrentBag<(string Pattern, RegexOptions Options, TimeSpan Timeout)> Requests { get; } = [];

    public bool TryCompile(string pattern, RegexOptions options, TimeSpan matchTimeout,
        Action checkpoint, [NotNullWhen(true)] out ISiftCompiledRegex? compiledPattern)
    {
        Interlocked.Increment(ref Compilations);
        Requests.Add((pattern, options, matchTimeout));
        checkpoint();
        BeforeCompile?.Invoke();
        checkpoint();
        if (TimeoutCompile) throw new RegexMatchTimeoutException();
        if (FailCompile) throw new IOException("Provider compilation failed.");
        compiledPattern = null;
        if (Decline) return false;
        if (ReturnNullOnSuccess) { compiledPattern = null!; return true; }
        var compiled = new Pattern(this, pattern);
        Patterns.Add(compiled);
        compiledPattern = compiled;
        AfterCompile?.Invoke();
        return !ReturnHandleOnDecline;
    }

    public void Dispose() => Disposed = true;

    internal sealed class Pattern(RegexContractTestEngine engine, string patternText) : ISiftCompiledRegex
    {
        public bool Disposed;
        public string PatternText { get; } = patternText;
        public SiftRegexInputDomain InputDomain => engine.InvalidDomain ? (SiftRegexInputDomain)(-1) : SiftRegexInputDomain.Ascii;
        public void EnumerateMatches(ReadOnlySpan<byte> input, ISiftRegexMatchSink sink, Action checkpoint)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            checkpoint();
            engine.BeforeMatch?.Invoke();
            checkpoint();
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (engine.TimeoutMatch) throw new RegexMatchTimeoutException();
            if (engine.InvalidRange) { sink.OnMatch(-1, 1); return; }
            if (engine.NoMatches) return;
            if (engine.Ranges is { } ranges)
            {
                foreach (var (start, length) in ranges)
                    if (!sink.OnMatch(start, length) && !engine.ContinueAfterStop) return;
            }
            else if (!sink.OnMatch(0, input.Length)) return;
            if (engine.TimeoutAfterMatch) throw new RegexMatchTimeoutException();
        }
        public void Dispose() => Disposed = true;
    }
}
