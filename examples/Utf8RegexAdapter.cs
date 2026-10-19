using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Lokad.Sift;
using ProducerRegex = Lokad.Utf8Regex.Utf8Regex;

namespace Lokad.Sift.Examples;

/// <summary>Application-side integration source; this file is not part of the Sift library.</summary>
/// <remarks>The application references Utf8Regex directly. Supply nativeCapability only when
/// a supported producer API guarantees native execution for the compiled pattern and domain.
/// The pinned 0.1.0 API has no such query: the default adapter compiles the real producer to
/// validate integration, then declines matching so Sift uses its safe managed verifier.</remarks>
internal sealed class Utf8RegexAdapter(
    Func<ProducerRegex, SiftRegexInputDomain?>? nativeCapability = null) : ISiftRegexEngine
{
    public bool TryCompile(string pattern, RegexOptions options, TimeSpan matchTimeout,
        Action checkpoint, [NotNullWhen(true)] out ISiftCompiledRegex? compiledPattern)
    {
        checkpoint();
        var regex = new ProducerRegex(pattern, options, matchTimeout);
        checkpoint();
        compiledPattern = null;
        if (nativeCapability?.Invoke(regex) is not { } domain) return false;
        compiledPattern = new CompiledPattern(regex, domain);
        return true;
    }

    private sealed class CompiledPattern(ProducerRegex regex, SiftRegexInputDomain domain) : ISiftCompiledRegex
    {
        private ProducerRegex? _regex = regex;
        public SiftRegexInputDomain InputDomain => domain;

        public void EnumerateMatches(ReadOnlySpan<byte> input, ISiftRegexMatchSink sink, Action checkpoint)
        {
            var active = _regex ?? throw new ObjectDisposedException(nameof(CompiledPattern));
            checkpoint();
            foreach (var match in active.EnumerateMatches(input))
            {
                checkpoint();
                if (!match.TryGetByteRange(out var start, out var length))
                    throw new InvalidOperationException("The producer returned a match outside UTF-8 byte boundaries.");
                if (!sink.OnMatch(start, length)) return;
            }
            checkpoint();
        }

        public void Dispose() => _regex = null;
    }
}
