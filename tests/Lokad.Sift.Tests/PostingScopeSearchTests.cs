namespace Lokad.Sift.Tests;

public sealed class PostingScopeSearchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostingSearchesRespectRangesAndVisibilityAcrossGenerations(bool disk)
    {
        using var fixture = new CorpusFixture();
        var options = new SiftOptions
        {
            TargetSegmentDocumentCount = 512,
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0
        };
        using var index = disk ? new SiftIndex(fixture.CorpusPath, options) : SiftIndex.CreateInMemory(options);
        var initial = Enumerable.Range(0, 1024).ToDictionary(n => $"src/{n:D4}.txt",
            n => n % 32 == 0 ? "needle alpha beta café 😀" : "ordinary café 😀", StringComparer.Ordinal);
        await index.Build(new InMemoryDocumentSource(initial.Select(entry => (entry.Key, entry.Value)).ToArray()));
        using var retained = index.OpenSnapshot();
        using var overlay = index.OpenOverlaySnapshot();
        (string, string)[] updates = [("src/0005.txt", "needle alpha beta"), ("src/0512.txt", "ordinary")];
        DocumentKey[] deleted = [new("src/0000.txt"), new("src/0032.txt")];
        await index.Update(new InMemoryDocumentSource(updates), deleted);
        await overlay.Update(new InMemoryDocumentSource(updates), deleted);
        var changed = new Dictionary<string, string>(initial, StringComparer.Ordinal);
        foreach (var (path, text) in updates) changed[path] = text;
        foreach (var key in deleted) changed.Remove(key.Path);
        using var current = index.OpenSnapshot();
        await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
        using var compacted = index.OpenSnapshot();

        foreach (var (snapshot, documents) in new[]
        {
            ((IIndexSnapshot)retained, initial), (current, changed), (overlay, changed), (compacted, changed)
        })
        foreach (var prefix in new[] { "", "src/00", "src/05", "src/000", "absent/" })
        foreach (var (pattern, mode) in new[]
        {
            ("needle", PatternMode.Literal), ("alpha.*beta", PatternMode.Regex), ("absent-marker", PatternMode.Literal)
        })
        {
            var expected = documents.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                entry.Value.Contains(pattern == "alpha.*beta" ? "alpha beta" : pattern, StringComparison.Ordinal))
                .Select(entry => entry.Key).Order(StringComparer.Ordinal).ToArray();
            var query = new SearchQuery(pattern, mode, CaseMode.Sensitive) { MaxHits = int.MaxValue, MaxHitsPerDocument = 1 };
            var unfiltered = new HitBuffer();
            snapshot.Search(query, new PathFilter(), unfiltered);
            var ordered = unfiltered.Hits.Select(hit => snapshot.GetDocumentKey(hit).Path)
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            Assert.Equal(expected, ordered.Order(StringComparer.Ordinal));
            var filter = new PathFilter { PathPrefix = prefix };
            var hits = new HitBuffer();
            snapshot.Search(query, filter, hits);
            Assert.Equal(ordered, hits.Hits.Select(hit => snapshot.GetDocumentKey(hit).Path));
            var limited = new HitBuffer();
            snapshot.Search(query with { MaxHits = 3 }, filter, limited);
            Assert.Equal(ordered.Take(3), limited.Hits.Select(hit => snapshot.GetDocumentKey(hit).Path));
        }
    }
}
