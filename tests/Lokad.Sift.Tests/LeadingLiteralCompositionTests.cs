using System.Text;
using System.Text.RegularExpressions;

namespace Lokad.Sift.Tests;

public sealed class LeadingLiteralCompositionTests
{
    [Theory]
    [InlineData("snapshot", "direct")]
    [InlineData("snapshot", "indexed")]
    [InlineData("snapshot", "default")]
    [InlineData("parallel", "direct")]
    [InlineData("parallel", "indexed")]
    [InlineData("parallel", "default")]
    [InlineData("overlay", "direct")]
    [InlineData("overlay", "indexed")]
    [InlineData("overlay", "default")]
    public async Task NestedGroupPrefixesPreserveManagedResults(string route, string planning)
    {
        (string Path, string Text)[] batch = [("a.txt", "fooXbarqux"), ("b.txt", "bazqux"), ("c.txt", "bazzy"),
            ("d.txt", "fooabarqux"), ("e.txt", "fooxxxbarqux"), ("decoy.txt", "fooqux"), ("other.txt", "other"),
            ("unicode.txt", "π\nfooébarqux\nbazqux"), ("astral.txt", "foo😀barqux"),
            ("upper.txt", "FOOxBARQUX BAZQUX"), ("lines.txt", "fooXbarqux\nbazqux\nfoo\nbarqux")];
        batch = batch.Concat(Enumerable.Range(0, 64).Select(i => ($"noise/{i}.txt", new string('z', 16384)))).ToArray();
        var documents = Enumerable.Range(0, 2).SelectMany(i => batch.Select(doc => ($"batch{i}/{doc.Path}", doc.Text))).ToArray();
        var defaults = new SiftOptions();
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = route == "parallel" ? batch.Length : defaults.TargetSegmentDocumentCount,
            DirectScanScopeDocumentsThreshold = planning == "indexed" ? 0 : planning == "default" ? defaults.DirectScanScopeDocumentsThreshold : 1000,
            DirectScanScopeBytesThreshold = planning == "indexed" ? 0 : planning == "default" ? defaults.DirectScanScopeBytesThreshold : 10 * 1024 * 1024,
            DirectScanCandidateDensityThreshold = planning == "default" ? defaults.DirectScanCandidateDensityThreshold : 1.1
        });
        await index.Build(new InMemoryDocumentSource(documents.Select(doc =>
            (doc.Item1, route == "overlay" ? "stale" : doc.Text)).ToArray()));
        using IIndexSnapshot snapshot = route == "overlay" ? index.OpenOverlaySnapshot(new SearchOptions(2)) :
            index.OpenSnapshot(new SearchOptions(route == "parallel" ? 2 : 1));
        if (snapshot is IOverlaySnapshot overlay) await overlay.Update(new InMemoryDocumentSource(documents), []);

        foreach (var pattern in new[] { "(?:foo.*bar|baz)qux", "(?:(?:foo.*bar)qux|bazzy)",
            "(?:(?:foo[ab]bar)qux|bazzy)", "(?:(?:foo.{1,3}bar)qux|bazzy)",
            "(?:(?:foo.*bar|baz)qux|other)", "(?:(?:foo[a-z]?bar)qux|bazzy)",
            "(?:(?:foo.*bar)+qux|bazzy)", "(?:foo|baz)qux", "(?:(?:foo|baz)qux|bazzy)" })
        foreach (var caseMode in new[] { CaseMode.Sensitive, CaseMode.Insensitive, CaseMode.Smart })
        foreach (var wholeLine in new[] { false, true })
        {
            var query = new SearchQuery(pattern, PatternMode.Regex, caseMode) { WholeLine = wholeLine };
            var regexOptions = RegexOptions.CultureInvariant |
                (caseMode == CaseMode.Sensitive ? RegexOptions.None : RegexOptions.IgnoreCase) |
                (wholeLine ? RegexOptions.Multiline : RegexOptions.None);
            var referencePattern = wholeLine ? $"^(?:{pattern})$" : pattern;
            var expected = documents.SelectMany(doc => Regex.Matches(doc.Text, referencePattern, regexOptions, TimeSpan.FromSeconds(1))
                .Select(match => (Path: doc.Item1, Start: Encoding.UTF8.GetByteCount(doc.Text.AsSpan(0, match.Index)),
                    End: Encoding.UTF8.GetByteCount(doc.Text.AsSpan(0, match.Index + match.Length)), Text: match.Value)))
                .OrderBy(hit => hit.Path, StringComparer.Ordinal).ThenBy(hit => hit.Start).ToArray();
            var hits = new HitBuffer();

            var stats = snapshot.Search(query, new(), hits);

            var actual = hits.Hits.Select(raw => (snapshot.Materialize(raw, 0, 0).Path,
                raw.StartByte, raw.EndByte, snapshot.Materialize(raw, 0, 0).MatchText))
                .OrderBy(hit => hit.Path, StringComparer.Ordinal).ThenBy(hit => hit.StartByte).ToArray();
            Assert.True(expected.SequenceEqual(actual), $"{route}/{planning}/{caseMode}/wholeLine={wholeLine}: {pattern}");
            Assert.Equal(expected.Length, stats.HitsReturned);
            Assert.Equal(0, stats.Utf8RegexInvocations);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NestedGroupSearchHonorsLimitsAndCancellation(int parallelism)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = 1,
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        var content = new string('z', 512) + " fooXbarqux bazzy fooYbarqux fooqux";
        await index.Build(new InMemoryDocumentSource([("a.txt", content), ("b.txt", content)]));
        using var snapshot = index.OpenSnapshot(new SearchOptions(parallelism));
        var query = new SearchQuery("(?:(?:foo.bar)qux|bazzy)", PatternMode.Regex, CaseMode.Sensitive);
        Assert.Equal(3, snapshot.Search(query with { MaxHits = 3 }, new(), new HitBuffer()).HitsReturned);
        Assert.Equal(2, snapshot.Search(query with { MaxHitsPerDocument = 1 }, new(), new HitBuffer()).HitsReturned);
        Assert.Equal(3, snapshot.Search(query with { MaxMatchedDocuments = 1 }, new(), new HitBuffer()).HitsReturned);
        using var cancellation = new CancellationTokenSource();
        var collector = new CancellingCollector(cancellation);
        Assert.Throws<OperationCanceledException>(() => snapshot.Search(query, new(), collector, cancellation.Token));
        Assert.Equal(1, collector.Hits);
    }

    private sealed class CancellingCollector(CancellationTokenSource cancellation) : IHitCollector
    {
        public int Hits { get; private set; }
        public bool OnHit(in RawHit hit) { Hits++; cancellation.Cancel(); return true; }
    }
}
