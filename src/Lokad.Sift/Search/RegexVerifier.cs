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

    public static void SearchDocument(
        MappedSegment segment,
        SegmentDocument document,
        PreparedRegexVerifier prepared,
        int hitLimit,
        IHitCollector collector,
        CancellationToken cancellationToken,
        ref long decodedChars,
        ref int hitsReturned,
        ref int regexInvocations,
        ref int nonBacktrackingRegexInvocations,
        ref SearchStopReason stopReason)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contentBytes = segment.GetContentBytes(document);
        var encodedLineIndex = segment.GetEncodedLineIndexBytes(document);
        var isAscii = (document.Flags & SegmentDocumentFlags.Ascii) != 0;
        using var decodedBuffer = RentedCharBuffer.Decode(contentBytes, isAscii, ref decodedChars);
        var text = decodedBuffer.AsSpan();

        regexInvocations++;
        if (prepared.UsesNonBacktracking)
        {
            nonBacktrackingRegexInvocations++;
        }
        var offsetCursor = new Utf8OffsetCursor();

        try
        {
            var matches = prepared.Regex.EnumerateMatches(text);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hasMatch = matches.MoveNext();
                cancellationToken.ThrowIfCancellationRequested();
                if (!hasMatch) break;
                var match = matches.Current;
                if (hitsReturned >= hitLimit)
                {
                    return;
                }

                if (prepared.WholeWord &&
                    !WordBoundary.IsWholeWord(text, match.Index, match.Index + match.Length))
                {
                    continue;
                }

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
                    new RawHit(
                        segment.SegmentId,
                        new DocumentOrdinal(document.Ordinal),
                        startByte,
                        endByte,
                        startLine,
                        endLine));
                hitsReturned++;
                if (!shouldContinue)
                {
                    stopReason = SearchStopReason.CollectorStopped;
                    return;
                }

                if (hitsReturned >= hitLimit)
                {
                    return;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            if (prepared.RegexTimeoutPolicy == RegexTimeoutPolicy.Throw)
            {
                throw;
            }

            stopReason = SearchStopReason.RegexTimeout;
        }
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
            query.RegexTimeoutPolicy);
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
}

internal sealed record PreparedRegexVerifier(
    Regex Regex,
    bool UsesNonBacktracking,
    bool WholeWord,
    RegexTimeoutPolicy RegexTimeoutPolicy);
