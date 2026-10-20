using System.Text;
using System.Text.RegularExpressions;
using Lokad.Sift;

namespace Lokad.Sift.Tests;

public sealed class RegexEnumerationTests
{
    [Theory]
    [InlineData("aa", "aaa")]
    [InlineData("(?:aa)", "aaaa")]
    [InlineData("aa{1}", "aaaaa")]
    [InlineData("aba", "ababa")]
    [InlineData(@"\.\.", "...")]
    [InlineData("éé", "ééé")]
    [InlineData("😀😀", "😀😀😀")]
    [InlineData("(?:)", "abc")]
    [InlineData("()", "é😀")]
    [InlineData("(?:|)", "abc")]
    [InlineData("(?:)", "")]
    [InlineData("a{0}", "abc")]
    public async Task RegexResultsAgreeWithManagedEnumeration(string pattern, string text)
    {
        foreach (var favorIndex in new[] { false, true })
        foreach (var caseMode in new[] { CaseMode.Sensitive, CaseMode.Insensitive, CaseMode.Smart })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions
            {
                DirectScanScopeDocumentsThreshold = favorIndex ? 0 : 256,
                DirectScanScopeBytesThreshold = favorIndex ? 0 : 1024 * 1024,
                DirectScanCandidateDensityThreshold = favorIndex ? 1.1 : 0.85
            });
            var documents = new[] { ("a.txt", text) }.Concat(Enumerable.Range(0, 64)
                .Select(i => ($"noise/{i:D2}.txt", ""))).ToArray();
            await index.Build(new InMemoryDocumentSource(documents));
            using var snapshot = index.OpenSnapshot();
            var query = new SearchQuery(pattern, PatternMode.Regex, caseMode);
            var expected = documents.SelectMany(document => ExpectedRanges(document.Item2, query)
                .Select(range => (document.Item1, range.Start, range.End))).ToArray();
            var hits = new HitBuffer();

            snapshot.Search(query, new PathFilter(), hits);

            Assert.Equal(expected, hits.Hits.Select(hit =>
                (snapshot.Materialize(hit, 0, 0).Path, hit.StartByte, hit.EndByte)));
            if (favorIndex && caseMode == CaseMode.Sensitive && expected.Any(hit => hit.End > hit.Start))
                Assert.True(snapshot.Explain(query, new PathFilter()).UsesIndex);
        }
    }

    [Theory]
    [InlineData(@"a\.a", "xa.a.a", true, false)]
    [InlineData(@"é\.é", "xé.é.é", true, false)]
    [InlineData("aa", "xaa aa", true, false)]
    [InlineData(@"a\na", "xa\na\na", false, true)]
    [InlineData("aa", "aa\r\naa\n", false, true)]
    [InlineData(@"aa\r", "aa\r\n", false, true)]
    [InlineData("aa", "aa\naa\n", true, true)]
    [InlineData("(?:)", "abc\n\n", false, true)]
    public async Task BoundaryOptionsPreserveRegexEnumeration(
        string pattern, string text, bool wholeWord, bool wholeLine)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", text)]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive)
        {
            WholeWord = wholeWord,
            WholeLine = wholeLine
        };
        var hits = new HitBuffer();

        snapshot.Search(query, new PathFilter(), hits);

        Assert.Equal(ExpectedRanges(text, query), hits.Hits.Select(hit => (hit.StartByte, hit.EndByte)));
    }

    [Theory]
    [InlineData("aa", "aaa", 2)]
    [InlineData("éé", "ééé", 2)]
    public async Task LiteralModeStillReturnsOverlappingMatches(string pattern, string text, int count)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", text)]));
        using var snapshot = index.OpenSnapshot();
        var hits = new HitBuffer();

        snapshot.Search(new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), hits);

        Assert.Equal(count, hits.Hits.Count);
    }

    [Theory]
    [InlineData(PatternMode.Literal)]
    [InlineData(PatternMode.Regex)]
    public async Task TrulyEmptyQueriesKeepThePathListingShortcut(PatternMode mode)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", "abc"), ("b.txt", "")]));
        using var snapshot = index.OpenSnapshot();
        var hits = new HitBuffer();

        var stats = snapshot.Search(new SearchQuery("", mode, CaseMode.Sensitive), new PathFilter(), hits);

        Assert.Equal(2, hits.Hits.Count);
        Assert.All(hits.Hits, hit => Assert.Equal((0, 0), (hit.StartByte, hit.EndByte)));
        Assert.Equal(0, stats.BytesScanned);
        Assert.Equal(0, stats.RegexInvocations);
    }

    [Theory]
    [InlineData("aa", "aaaaa", "snapshot")]
    [InlineData("aa", "aaaaa", "overlay")]
    [InlineData("aa", "aaaaa", "parallel")]
    [InlineData("(?:)", "é😀", "snapshot")]
    [InlineData("(?:)", "é😀", "overlay")]
    [InlineData("(?:)", "é😀", "parallel")]
    public async Task EnumerationAndPerDocumentLimitsWorkAcrossSnapshots(
        string pattern, string text, string mode)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { TargetSegmentDocumentCount = 1 });
        await index.Build(new InMemoryDocumentSource([("a.txt", text), ("b.txt", text)]));
        using IIndexSnapshot snapshot = mode == "overlay"
            ? index.OpenOverlaySnapshot()
            : index.OpenSnapshot(new SearchOptions(mode == "parallel" ? 2 : 1));
        if (snapshot is IOverlaySnapshot overlay)
            await overlay.Update(new InMemoryDocumentSource([("a.txt", text), ("c.txt", text)]),
                [new DocumentKey("b.txt")]);
        var paths = mode == "overlay" ? new[] { "a.txt", "c.txt" } : new[] { "a.txt", "b.txt" };
        foreach (var maxHitsPerDocument in new[] { 1, int.MaxValue })
        {
            var query = new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive)
                { MaxHitsPerDocument = maxHitsPerDocument };
            var expected = paths.SelectMany(path => ExpectedRanges(text, query).Take(maxHitsPerDocument)
                .Select(range => (path, range.Start, range.End))).ToArray();
            var hits = new HitBuffer();

            snapshot.Search(query, new PathFilter(), hits);

            Assert.Equal(expected, hits.Hits.Select(hit =>
                (snapshot.Materialize(hit, 0, 0).Path, hit.StartByte, hit.EndByte))
                .OrderBy(hit => hit.Path, StringComparer.Ordinal));
        }
    }

    [Theory]
    [InlineData("aa", "aaaaaa")]
    [InlineData("(?:)", "abc")]
    public async Task EnumerationRespectsTotalHitLimitsAndCollectorStops(string pattern, string text)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", text)]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive) { MaxHits = 2 };
        var hits = new HitBuffer();

        var limited = snapshot.Search(query, new PathFilter(), hits);

        Assert.Equal(ExpectedRanges(text, query).Take(2), hits.Hits.Select(hit => (hit.StartByte, hit.EndByte)));
        Assert.Equal(SearchStopReason.MaxHits, limited.StopReason);
        var stopping = new CallbackCollector(() => false);
        var stopped = snapshot.Search(query, new PathFilter(), stopping);
        Assert.Equal(1, stopping.Count);
        Assert.Equal(1, stopped.HitsReturned);
        Assert.Equal(SearchStopReason.CollectorStopped, stopped.StopReason);
    }

    [Fact]
    public async Task EmptyGroupEnumerationObservesCancellationBetweenHits()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", "abc")]));
        using var snapshot = index.OpenSnapshot();
        using var cancellation = new CancellationTokenSource();
        var collector = new CallbackCollector(() => { cancellation.Cancel(); return true; });

        Assert.ThrowsAny<OperationCanceledException>(() => snapshot.Search(
            new SearchQuery("(?:)", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector, cancellation.Token));

        Assert.Equal(1, collector.Count);
    }

    private sealed class CallbackCollector(Func<bool> callback) : IHitCollector
    {
        public int Count { get; private set; }
        public bool OnHit(in RawHit hit) { Count++; return callback(); }
    }

    private static (int Start, int End)[] ExpectedRanges(string text, SearchQuery query)
    {
        var options = RegexOptions.CultureInvariant;
        if (query.CaseMode == CaseMode.Insensitive ||
            query.CaseMode == CaseMode.Smart && !query.Pattern.Any(char.IsUpper))
            options |= RegexOptions.IgnoreCase;
        if (query.Multiline || query.WholeLine) options |= RegexOptions.Multiline;
        if (query.DotAll) options |= RegexOptions.Singleline;
        var pattern = query.WholeLine ? $"^(?:{query.Pattern})$" : query.Pattern;
        return Regex.Matches(text, pattern, options)
            .Where(match => !query.WholeWord ||
                (match.Index == 0 || !IsWord(text[match.Index - 1])) &&
                (match.Index + match.Length == text.Length || !IsWord(text[match.Index + match.Length])))
            .Select(match => (ProjectOffset(match.Index), ProjectOffset(match.Index + match.Length)))
            .ToArray();

        int ProjectOffset(int index)
        {
            // Sift projects a UTF-16 position inside a surrogate pair onto the
            // scalar's starting UTF-8 boundary, including repeated empty hits.
            if (index > 0 && index < text.Length &&
                char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index])) index--;
            return Encoding.UTF8.GetByteCount(text.AsSpan(0, index));
        }

        static bool IsWord(char value) => char.IsLetterOrDigit(value) || value == '_';
    }
}
