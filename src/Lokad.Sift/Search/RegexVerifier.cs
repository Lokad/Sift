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
    private static readonly BoundedLruCache<RegexCacheKey, RegexEntry> Cache = new(CacheCapacity);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);
    private static long _cacheHits;
    private static long _cacheMisses;

    public static long CacheHits => Interlocked.Read(ref _cacheHits);
    public static long CacheMisses => Interlocked.Read(ref _cacheMisses);
    public static int CacheCount => Cache.Count;

    public static bool TrySearchDocument(
        MappedSegment segment,
        SegmentDocument document,
        in SearchQuery query,
        CaseMode resolvedCaseMode,
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

        var entry = GetRegexEntry(query, resolvedCaseMode);
        var regex = entry.Regex;
        regexInvocations++;
        if (entry.UsesNonBacktracking)
        {
            nonBacktrackingRegexInvocations++;
        }
        var matched = false;
        var isAscii = (document.Flags & (1 << 1)) != 0;
        int[]? utf8PrefixByteOffsets = null;

        try
        {
            foreach (var match in regex.EnumerateMatches(text))
            {
                if (hitsReturned >= hitLimit)
                {
                    return matched;
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
                    var prefixByteOffsets = utf8PrefixByteOffsets ??= BuildUtf8PrefixByteOffsets(text);
                    startByte = prefixByteOffsets[match.Index];
                    endByte = prefixByteOffsets[match.Index + match.Length];
                }
                var startLine = LineMap.GetLineFromByteOffset(encodedLineIndex, startByte);
                var endLine = LineMap.GetLineFromByteOffset(encodedLineIndex, Math.Max(startByte, endByte == 0 ? startByte : endByte - 1));
                var shouldContinue = collector.OnHit(
                    new RawHit(segment.SegmentId, document.Ordinal, startByte, endByte, startLine, endLine));
                hitsReturned++;
                if (!shouldContinue)
                {
                    stopReason = SearchStopReason.Cancelled;
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
            if (query.TimeoutPolicy == TimeoutPolicy.Throw)
            {
                throw;
            }

            stopReason = SearchStopReason.RegexTimeout;
            return false;
        }

        return matched;

        static int[] BuildUtf8PrefixByteOffsets(ReadOnlySpan<char> text)
        {
            var offsets = GC.AllocateUninitializedArray<int>(text.Length + 1);
            var byteOffset = 0;
            var index = 0;
            offsets[0] = 0;
            while (index < text.Length)
            {
                var current = text[index];
                if (char.IsHighSurrogate(current) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    offsets[index + 1] = byteOffset;
                    byteOffset += 4;
                    offsets[index + 2] = byteOffset;
                    index += 2;
                    continue;
                }

                byteOffset += current switch
                {
                    <= '\u007F' => 1,
                    <= '\u07FF' => 2,
                    _ => 3
                };
                offsets[index + 1] = byteOffset;
                index++;
            }

            return offsets;
        }
    }

    public static string BuildPattern(in SearchQuery query)
    {
        var pattern = query.Pattern;
        if (query.WholeWord)
        {
            pattern = $@"\b(?:{pattern})\b";
        }

        if (query.WholeLine)
        {
            pattern = $@"^(?:{pattern})$";
        }

        return pattern;
    }

    public static bool CanUseNonBacktracking(in SearchQuery query, CaseMode resolvedCaseMode)
    {
        return GetRegexEntry(query, resolvedCaseMode).UsesNonBacktracking;
    }

    public static Regex GetRegex(in SearchQuery query, CaseMode resolvedCaseMode)
    {
        return GetRegexEntry(query, resolvedCaseMode).Regex;
    }

    private static RegexEntry GetRegexEntry(in SearchQuery query, CaseMode resolvedCaseMode)
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
