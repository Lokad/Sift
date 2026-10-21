using System.Text;
using System.Text.RegularExpressions;

namespace Lokad.Sift.Tests;

public sealed class RegexCandidateCorrectnessTests
{
    [Theory]
    [InlineData("foo.bar", "fooébar")]
    [InlineData("foo.{1}bar", "foo中bar")]
    [InlineData("foo..bar", "foo😀bar")]
    [InlineData("foo.{2}bar", "foo😀bar")]
    [InlineData("foo[^x]bar", "fooébar")]
    [InlineData("foo[^x]{1,2}bar", "fooéébar")]
    [InlineData("aa.*aa", "aaaaa")]
    [InlineData("aa.{0,2}aa", "aaaaa")]
    [InlineData("aba.*aba", "abababa")]
    [InlineData("ana.*ana", "ananana")]
    [InlineData("aa.*aa.*aa", "aaaaaaa")]
    [InlineData("foo[ab]bar", "fooabar")]
    [InlineData("foo.bar", "foo\rbar")]
    [InlineData("foo.bar", "foo\nbar")]
    [InlineData("foo.*bar", "fooébar")]
    [InlineData("foo.*foo", "foofoo")]
    [InlineData(@"foo[^\r\n]*bar", "foo\rbar")]
    [InlineData("foo.bar", "FOOéBAR")]
    [InlineData("aa.*aa", "AAAAA")]
    public async Task CandidateFilteringPreservesManagedRegexResults(string pattern, string text)
    {
        foreach (var mode in new[] { "snapshot", "parallel", "overlay" })
        foreach (var favorIndex in new[] { false, true })
        foreach (var caseMode in new[] { CaseMode.Sensitive, CaseMode.Insensitive, CaseMode.Smart })
        {
            // Long documents make indexed plans cheaper even in a one-document segment.
            var content = (favorIndex ? new string('x', 256) + "\n" : "") + text;
            var documents = new[] { (Path: "a.txt", Text: content), (Path: "b.txt", Text: content) };
            using var index = SiftIndex.CreateInMemory(new SiftOptions
            {
                TargetSegmentDocumentCount = 1,
                DirectScanScopeDocumentsThreshold = favorIndex ? 0 : 256,
                DirectScanScopeBytesThreshold = favorIndex ? 0 : 1024 * 1024,
                DirectScanCandidateDensityThreshold = 1.1
            });
            await index.Build(new InMemoryDocumentSource(documents
                .Select(doc => (doc.Path, mode == "overlay" ? "stale" : doc.Text)).ToArray()));
            using IIndexSnapshot snapshot = mode == "overlay"
                ? index.OpenOverlaySnapshot(new SearchOptions(2))
                : index.OpenSnapshot(new SearchOptions(mode == "parallel" ? 2 : 1));
            if (snapshot is IOverlaySnapshot overlay)
                await overlay.Update(new InMemoryDocumentSource(documents), []);
            var query = new SearchQuery(pattern, PatternMode.Regex, caseMode);
            var options = RegexOptions.CultureInvariant |
                (caseMode == CaseMode.Sensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            var expected = documents.SelectMany(doc => Regex.Matches(doc.Text, pattern, options, TimeSpan.FromSeconds(1))
                .Select(match => (doc.Path, Start: Encoding.UTF8.GetByteCount(doc.Text.AsSpan(0, match.Index)),
                    End: Encoding.UTF8.GetByteCount(doc.Text.AsSpan(0, match.Index + match.Length)), Text: match.Value)))
                .OrderBy(hit => hit.Path, StringComparer.Ordinal).ThenBy(hit => hit.Start).ToArray();
            var hits = new HitBuffer();

            var stats = snapshot.Search(query, new(), hits);

            Assert.Equal(expected, hits.Hits.Select(raw =>
                {
                    var hit = snapshot.Materialize(raw, 0, 0);
                    return (hit.Path, raw.StartByte, raw.EndByte, hit.MatchText);
                })
                .OrderBy(hit => hit.Path, StringComparer.Ordinal).ThenBy(hit => hit.StartByte));
            Assert.Equal(expected.Length, stats.HitsReturned);
            if (caseMode == CaseMode.Sensitive)
                Assert.Equal(favorIndex, snapshot.Explain(query, new()).UsesIndex);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RegexWithOverlappingLiteralOccurrencesHonorsLimitsAndCancellation(int parallelism)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { TargetSegmentDocumentCount = 1 });
        await index.Build(new InMemoryDocumentSource([("a.txt", "aaaaaaaaaa"), ("b.txt", "aaaaaaaaaa")]));
        using var snapshot = index.OpenSnapshot(new SearchOptions(parallelism));
        var query = new SearchQuery("aa.{0,1}aa", PatternMode.Regex, CaseMode.Sensitive);
        Assert.Equal(3, snapshot.Search(query with { MaxHits = 3 }, new(), new HitBuffer()).HitsReturned);
        Assert.Equal(2, snapshot.Search(query with { MaxHitsPerDocument = 1 }, new(), new HitBuffer()).HitsReturned);
        Assert.Equal(2, snapshot.Search(query with { MaxMatchedDocuments = 1 }, new(), new HitBuffer()).HitsReturned);
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
