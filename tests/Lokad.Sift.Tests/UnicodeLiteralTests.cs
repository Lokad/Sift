using System.Text;

namespace Lokad.Sift.Tests;

public sealed class UnicodeLiteralTests
{
    [Theory]
    [InlineData(CaseMode.Sensitive)]
    [InlineData(CaseMode.Insensitive)]
    [InlineData(CaseMode.Smart)]
    public async Task LiteralOffsetsAgreeWithOverlappingUtf16Search(CaseMode caseMode)
    {
        (string, string)[] texts = [("a.txt", "😀x😀"), ("b.txt", "😀😀😀"),
            ("c.txt", "A😀 a😀 😀B 😀b"), ("d.txt", "ééé alpha"),
            ("uppercase.txt", "ÉéÉ CAFÉ 𐐀𐐨"), ("replacement.txt", "�x�"), ("empty.txt", "")];
        // Construct malformed UTF-16 at runtime; attribute strings can replace it.
        var malformed = new[] { "\uD83D", "\uDE00", "x\uD83D", "\uDE00x", "A\uD83D",
            "\uDE00B", "\uDE00\uD83D", "\uDE00😀\uD83D", "\uD83D\uD83D" };
        var patterns = malformed.Concat(["😀", "😀😀", "éé", "É", "CAFÉ", "𐐀", "alpha", "x", "�"]);
        foreach (var favorIndex in new[] { false, true })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions
            {
                DirectScanScopeDocumentsThreshold = favorIndex ? 0 : 256,
                DirectScanScopeBytesThreshold = favorIndex ? 0 : 1024 * 1024,
                DirectScanCandidateDensityThreshold = 1.1
            });
            await index.Build(new InMemoryDocumentSource(texts.Concat(Enumerable.Range(0, 64)
                .Select(i => ($"noise/{i:D2}.txt", ""))).ToArray()));
            using var snapshot = index.OpenSnapshot();
            foreach (var pattern in patterns)
            {
                var comparison = caseMode == CaseMode.Sensitive ||
                    caseMode == CaseMode.Smart && pattern.EnumerateRunes().Any(Rune.IsUpper)
                    ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var expected = texts.SelectMany(document => Find(document.Item1, document.Item2, pattern, comparison))
                    .OrderBy(hit => hit.Path, StringComparer.Ordinal).ToArray();
                var query = new SearchQuery(pattern, PatternMode.Literal, caseMode);
                var hits = new HitBuffer();

                var stats = snapshot.Search(query, new(), hits);
                var plan = snapshot.Explain(query, new());

                Assert.Equal(expected, hits.Hits.Select(hit =>
                    (snapshot.Materialize(hit, 0, 0).Path, hit.StartByte, hit.EndByte))
                    .OrderBy(hit => hit.Path, StringComparer.Ordinal));
                Assert.Equal(0, stats.RegexInvocations);
                if (malformed.Contains(pattern))
                {
                    Assert.False(plan.UsesIndex);
                    Assert.Empty(plan.MandatoryFragments);
                    Assert.True(plan.NeedsUnicodeDecode);
                }
                foreach (var raw in hits.Hits)
                {
                    var materialized = snapshot.Materialize(raw, 0, 0);
                    var text = texts.Single(document => document.Item1 == materialized.Path).Item2;
                    var bytes = Encoding.UTF8.GetBytes(text);
                    Assert.True(raw.StartByte == bytes.Length || (bytes[raw.StartByte] & 0xC0) != 0x80);
                    Assert.True(raw.EndByte == bytes.Length || (bytes[raw.EndByte] & 0xC0) != 0x80);
                    Assert.Equal(Encoding.UTF8.GetString(bytes.AsSpan(raw.StartByte, raw.EndByte - raw.StartByte)),
                        materialized.MatchText);
                }
            }
        }
    }

    [Fact]
    public async Task MalformedLiteralsCannotSupplyByteConstraints()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0, DirectScanScopeBytesThreshold = 0
        });
        await index.Build(new InMemoryDocumentSource([("a.txt", "😀x😀")]));
        using var snapshot = index.OpenSnapshot();
        foreach (var pattern in new[] { "\uD83D", "\uDE00", "\uDE00😀\uD83D" })
        {
            var plan = snapshot.Explain(new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive), new());
            Assert.False(plan.UsesIndex);
            Assert.Empty(plan.SelectedNgrams);
            Assert.Empty(plan.MandatoryFragments);
            Assert.True(plan.CanUseLiteralVerifier);
            Assert.True(plan.NeedsUnicodeDecode);
        }
    }

    [Theory]
    [InlineData("aa")]
    [InlineData("éé")]
    [InlineData("😀😀")]
    public async Task WellFormedLiteralsRetainIndexedOverlappingMatches(string pattern)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        var text = pattern + pattern;
        await index.Build(new InMemoryDocumentSource(new[] { ("a.txt", text) }
            .Concat(Enumerable.Range(0, 64).Select(i => ($"noise/{i:D2}.txt", ""))).ToArray()));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive);
        var hits = new HitBuffer();

        snapshot.Search(query, new(), hits);

        Assert.True(snapshot.Explain(query, new()).UsesIndex);
        Assert.Equal(Find("a.txt", text, pattern, StringComparison.Ordinal), hits.Hits.Select(hit =>
            ("a.txt", hit.StartByte, hit.EndByte)));
        Assert.Equal(3, hits.Hits.Count);
        Assert.All(hits.Hits, hit => Assert.Equal(pattern, snapshot.Materialize(hit, 0, 0).MatchText));

        var regexHits = new HitBuffer();
        snapshot.Search(query with { PatternMode = PatternMode.Regex }, new(), regexHits);
        var width = Encoding.UTF8.GetByteCount(pattern);
        Assert.Equal(new[] { (0, width), (width, 2 * width) },
            regexHits.Hits.Select(hit => (hit.StartByte, hit.EndByte)));
    }

    [Fact]
    public async Task SurrogateLiteralBoundariesAndPathFiltersRemainEffective()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "😀\n"), ("other.txt", "😀\n")]));
        using var snapshot = index.OpenSnapshot();
        var filter = new PathFilter { PathPrefix = "src/" };
        foreach (var pattern in new[] { "\uD83D", "\uDE00" })
        {
            var query = new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive);
            Assert.Equal(1, snapshot.Search(query, filter, new HitBuffer()).HitsReturned);
            Assert.Equal(0, snapshot.Search(query with { WholeWord = true }, filter, new HitBuffer()).HitsReturned);
            Assert.Equal(0, snapshot.Search(query with { WholeLine = true }, filter, new HitBuffer()).HitsReturned);
            Assert.False(snapshot.Explain(query, new PathFilter { PathPrefix = "missing/" }).NeedsUnicodeDecode);
        }
    }

    [Theory]
    [InlineData("é", CaseMode.Sensitive)]
    [InlineData("😀", CaseMode.Sensitive)]
    [InlineData("É", CaseMode.Smart)]
    [InlineData("𐐀", CaseMode.Smart)]
    [InlineData("é", CaseMode.Smart)]
    public async Task UnicodeLiteralsHonorLimitsAndCancellation(string pattern, CaseMode caseMode)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", pattern + pattern + pattern), ("b.txt", pattern)]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery(pattern, PatternMode.Literal, caseMode);
        var hits = new HitBuffer();
        var stats = snapshot.Search(query with { MaxHits = 2 }, new(), hits);
        var width = Encoding.UTF8.GetByteCount(pattern);
        Assert.Equal(SearchStopReason.MaxHits, stats.StopReason);
        Assert.Equal(new[] { (0, width), (width, 2 * width) }, hits.Hits.Select(hit => (hit.StartByte, hit.EndByte)));
        Assert.Equal(2, snapshot.Search(query with { MaxHitsPerDocument = 1 }, new(), new HitBuffer()).HitsReturned);
        Assert.Equal(3, snapshot.Search(query with { MaxMatchedDocuments = 1 }, new(), new HitBuffer()).HitsReturned);
        using var cancellation = new CancellationTokenSource();
        var cancelling = new CancellingCollector(cancellation);
        Assert.Throws<OperationCanceledException>(() => snapshot.Search(query, new(), cancelling, cancellation.Token));
        Assert.Equal(1, cancelling.Hits);
    }

    [Fact]
    public async Task SurrogateLiteralsHonorLimitsAndCancellation()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", "😀😀😀"), ("b.txt", "😀")]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("\uD83D", PatternMode.Literal, CaseMode.Sensitive);
        var hits = new HitBuffer();
        var stats = snapshot.Search(query with { MaxHits = 2 }, new(), hits);
        Assert.Equal(SearchStopReason.MaxHits, stats.StopReason);
        Assert.Equal(new[] { (0, 0), (4, 4) }, hits.Hits.Select(hit => (hit.StartByte, hit.EndByte)));
        Assert.Equal(2, snapshot.Search(query with { MaxHitsPerDocument = 1 }, new(), new HitBuffer()).HitsReturned);
        Assert.Equal(3, snapshot.Search(query with { MaxMatchedDocuments = 1 }, new(), new HitBuffer()).HitsReturned);
        using var cancellation = new CancellationTokenSource();
        var cancelling = new CancellingCollector(cancellation);
        Assert.Throws<OperationCanceledException>(() => snapshot.Search(query, new(), cancelling, cancellation.Token));
        Assert.Equal(1, cancelling.Hits);
    }

    [Fact]
    public async Task SurrogateLiteralProjectionSurvivesOverlayUpdateAndCompaction()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", "😀x😀")]));
        using var old = index.OpenSnapshot();
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new InMemoryDocumentSource([("a.txt", "😁"), ("b.txt", "😀😀😀")]), []);
        var query = new SearchQuery("\uD83D", PatternMode.Literal, CaseMode.Sensitive);
        var overlayHits = new HitBuffer();
        overlay.Search(query, new(), overlayHits);
        Assert.Equal(new[] { ("a.txt", 0, 0), ("b.txt", 0, 0), ("b.txt", 4, 4), ("b.txt", 8, 8) },
            overlayHits.Hits.Select(hit => (overlay.Materialize(hit, 0, 0).Path, hit.StartByte, hit.EndByte)));
        await index.Update(new InMemoryDocumentSource([("a.txt", "😀😀😀")]), []);
        Assert.True((await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 })).InputSegments > 0);
        using var current = index.OpenSnapshot();
        var currentHits = new HitBuffer();
        current.Search(query, new(), currentHits);
        Assert.Equal(new[] { (0, 0), (4, 4), (8, 8) }, currentHits.Hits.Select(hit => (hit.StartByte, hit.EndByte)));
        var oldHits = new HitBuffer();
        old.Search(query, new(), oldHits);
        Assert.Equal(new[] { (0, 0), (5, 5) }, oldHits.Hits.Select(hit => (hit.StartByte, hit.EndByte)));
        Assert.All(oldHits.Hits, hit => Assert.Empty(old.Materialize(hit, 0, 0).MatchText));
    }

    private static IEnumerable<(string Path, int Start, int End)> Find(
        string path, string text, string pattern, StringComparison comparison)
    {
        for (var start = 0; start <= text.Length;)
        {
            var found = text.IndexOf(pattern, start, comparison);
            if (found < 0) yield break;
            yield return (path, Project(text, found), Project(text, found + pattern.Length));
            start = found + 1;
        }
    }

    private static int Project(string text, int position)
    {
        if (position > 0 && position < text.Length &&
            char.IsHighSurrogate(text[position - 1]) && char.IsLowSurrogate(text[position]))
            position--;
        return Encoding.UTF8.GetByteCount(text.AsSpan(0, position));
    }

    private sealed class CancellingCollector(CancellationTokenSource cancellation) : IHitCollector
    {
        public int Hits { get; private set; }
        public bool OnHit(in RawHit hit) { Hits++; cancellation.Cancel(); return true; }
    }
}
