using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using Lokad.Sift;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal static class RegexVerifier
{
    internal const int CacheCapacity = 256;
    internal const int CachePatternCharacterCapacity = 256 * 1024;
    private static readonly BoundedLruCache<RegexCacheKey, RegexEntry> Cache = new(
        CacheCapacity,
        CachePatternCharacterCapacity,
        static key => key.Pattern.Length);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);
    private static long _cacheHits;
    private static long _cacheMisses;

    public static long CacheHits => Interlocked.Read(ref _cacheHits);
    public static long CacheMisses => Interlocked.Read(ref _cacheMisses);
    public static int CacheCount => Cache.Count;
    public static int CachePatternCharacters => Cache.Weight;
    internal static void RecordPreparedCacheHit() => Interlocked.Increment(ref _cacheHits);

    public static bool TrySearchDocument(
        MappedSegment segment,
        SegmentDocument document,
        PreparedRegexVerifier prepared,
        int hitLimit,
        IHitCollector collector,
        ref long decodedChars,
        ref int hitsReturned,
        ref int regexInvocations,
        ref int nonBacktrackingRegexInvocations,
        ref SearchStopReason stopReason,
        string? predecodedText)
    {
        var contentBytes = segment.GetContentBytes(document);
        var encodedLineIndex = segment.GetEncodedLineIndexBytes(document);
        using var decodedBuffer = predecodedText is null
            ? RentedCharBuffer.Decode(contentBytes, ref decodedChars)
            : default;
        var text = predecodedText is not null
            ? predecodedText.AsSpan()
            : decodedBuffer.AsSpan();

        regexInvocations++;
        if (prepared.UsesNonBacktracking)
        {
            nonBacktrackingRegexInvocations++;
        }
        var matched = false;
        var isAscii = (document.Flags & (1 << 1)) != 0;
        var offsetCursor = new Utf8OffsetCursor();

        try
        {
            foreach (var match in prepared.Regex.EnumerateMatches(text))
            {
                if (hitsReturned >= hitLimit)
                {
                    return matched;
                }

                if (prepared.WholeWord &&
                    !WordBoundary.IsWholeWord(text, match.Index, match.Index + match.Length))
                {
                    continue;
                }

                matched = true;
                int startByte;
                int endByte;
                if (isAscii)
                {
                    startByte = match.Index;
                    endByte = startByte + match.Length;
                }
                else
                {
                    // Regex matches are ordered, so one cursor maps their UTF-16 offsets without a document-sized array.
                    startByte = offsetCursor.Map(text, match.Index);
                    endByte = offsetCursor.Map(text, match.Index + match.Length);
                }
                var startLine = LineMap.GetLineFromByteOffset(encodedLineIndex, startByte);
                var endLine = LineMap.GetLineFromByteOffset(encodedLineIndex, Math.Max(startByte, endByte == 0 ? startByte : endByte - 1));
                var shouldContinue = collector.OnHit(
                    new RawHit(segment.SegmentId, document.Ordinal, startByte, endByte, startLine, endLine));
                hitsReturned++;
                if (!shouldContinue)
                {
                    stopReason = SearchStopReason.CollectorStopped;
                    return true;
                }

                if (hitsReturned >= hitLimit)
                {
                    return true;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            if (prepared.TimeoutPolicy == TimeoutPolicy.Throw)
            {
                throw;
            }

            stopReason = SearchStopReason.RegexTimeout;
            return false;
        }

        return matched;
    }

    public static string BuildPattern(SearchQuery query)
    {
        return BuildPattern(query, includeWholeWord: false);
    }

    public static string BuildUtf8Pattern(SearchQuery query)
    {
        return BuildPattern(query, includeWholeWord: true);
    }

    private static string BuildPattern(SearchQuery query, bool includeWholeWord)
    {
        var pattern = query.Pattern;
        if (includeWholeWord && query.WholeWord)
        {
            pattern = $@"\b(?:{pattern})\b";
        }

        if (query.WholeLine)
        {
            pattern = $@"^(?:{pattern})$";
        }

        return pattern;
    }

    public static PreparedRegexVerifier Prepare(SearchQuery query, CaseMode resolvedCaseMode)
    {
        var entry = GetRegexEntry(query, resolvedCaseMode);
        return new PreparedRegexVerifier(
            entry.Regex,
            entry.UsesNonBacktracking,
            query.WholeWord,
            query.TimeoutPolicy);
    }

    public static Regex GetRegex(SearchQuery query, CaseMode resolvedCaseMode)
    {
        return Prepare(query, resolvedCaseMode).Regex;
    }

    private static RegexEntry GetRegexEntry(SearchQuery query, CaseMode resolvedCaseMode)
    {
        var pattern = BuildPattern(query);
        var options = RegexOptions.CultureInvariant;
        if (query.Multiline || query.WholeLine)
        {
            options |= RegexOptions.Multiline;
        }

        if (query.DotAll)
        {
            options |= RegexOptions.Singleline;
        }

        if (resolvedCaseMode == CaseMode.Insensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        var key = new RegexCacheKey(pattern, options);
        var entry = Cache.GetOrAdd(key, static cacheKey => CreateRegex(cacheKey.Pattern, cacheKey.Options), out var cacheHit);
        if (cacheHit)
        {
            Interlocked.Increment(ref _cacheHits);
        }
        else
        {
            Interlocked.Increment(ref _cacheMisses);
        }

        return entry;
    }

    private static RegexEntry CreateRegex(string pattern, RegexOptions options)
    {
        try
        {
            return new RegexEntry(new Regex(pattern, options | RegexOptions.NonBacktracking, DefaultTimeout), true);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new RegexEntry(new Regex(pattern, options, DefaultTimeout), false);
        }
    }

    private readonly record struct RegexCacheKey(string Pattern, RegexOptions Options);
    private readonly record struct RegexEntry(Regex Regex, bool UsesNonBacktracking);

    private struct RentedCharBuffer : IDisposable
    {
        private char[]? _buffer;
        private int _length;

        public static RentedCharBuffer Decode(ReadOnlySpan<byte> utf8, ref long decodedChars)
        {
            var charCount = Encoding.UTF8.GetCharCount(utf8);
            decodedChars += charCount;
            if (charCount == 0)
            {
                return default;
            }

            var buffer = ArrayPool<char>.Shared.Rent(charCount);
            var charsWritten = Encoding.UTF8.GetChars(utf8, buffer);
            return new RentedCharBuffer
            {
                _buffer = buffer,
                _length = charsWritten
            };
        }

        public ReadOnlySpan<char> AsSpan()
        {
            return _buffer is null ? [] : _buffer.AsSpan(0, _length);
        }

        public void Dispose()
        {
            if (_buffer is not null)
            {
                ArrayPool<char>.Shared.Return(_buffer);
                _buffer = null;
                _length = 0;
            }
        }
    }
}

internal sealed record PreparedRegexVerifier(
    Regex Regex,
    bool UsesNonBacktracking,
    bool WholeWord,
    TimeoutPolicy TimeoutPolicy);
