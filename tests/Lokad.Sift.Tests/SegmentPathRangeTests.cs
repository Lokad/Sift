namespace Lokad.Sift.Tests;

public sealed class SegmentPathRangeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactScopesPreserveOrderAcrossOverlappingAndRetainedGenerations(bool disk)
    {
        using var fixture = new CorpusFixture();
        var options = new SiftOptions
        {
            TargetSegmentDocumentCount = 64,
            MaxPathFilterPatterns = 1024,
            MaxPathFilterCharacters = 32 * 1024
        };
        using var index = disk ? new SiftIndex(fixture.CorpusPath, options) : SiftIndex.CreateInMemory(options);
        var paths = Enumerable.Range(0, 100).Select(n => $"src/{n:D4}.txt")
            .Concat(["src/café.txt", "src/𐀀.txt", "src/😀.txt", "src/\ue000.txt"]).ToArray();
        await index.Build(new InMemoryDocumentSource(paths.Select(path => (path, "needle")).ToArray()));
        using var retained = index.OpenSnapshot();
        using var overlay = index.OpenOverlaySnapshot();
        (string, string)[] updates = [("src/0001.txt", "needle updated"), ("src/0050.txt", "gone"),
            ("src/0100.txt", "needle"), ("src/😀.txt", "needle updated")];
        DocumentKey[] deleted = [new("src/0000.txt"), new("src/0099.txt")];
        await index.Update(new InMemoryDocumentSource(updates), deleted);
        await overlay.Update(new InMemoryDocumentSource(updates), deleted);
        using var current = index.OpenSnapshot();
        await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
        using var compacted = index.OpenSnapshot();

        string[] chosen = ["src/0000.txt", "src/0001.txt", "src/0007.txt", "src/0008.txt", "src/0050.txt",
            "src/0099.txt", "src/0100.txt", "src/𐀀.txt", "src/😀.txt", "src/\ue000.txt"];
        var includes = Enumerable.Range(0, 512).Select(n => $"absent/{n:D4}.txt")
            .Concat(chosen.Reverse()).Concat(["src\\😀.txt", "src/0007a.txt", "zzz/after.txt"]).ToArray();
        var sparseIncludes = chosen.Reverse().Concat(["src\\😀.txt", "src/0007a.txt", "zzz/after.txt"]).ToArray();
        var accepted = chosen.ToHashSet(StringComparer.Ordinal);
        foreach (var includeScope in new[] { sparseIncludes, includes })
        foreach (var snapshot in new IIndexSnapshot[] { retained, current, overlay, compacted })
        foreach (var pattern in new[] { "", "needle" })
        foreach (var remainder in new PathFilter[]
        {
            new(), new() { PathPrefix = "src/000" }, new() { PathPrefix = "src/00" }, new() { PathPrefix = "none/" },
            new() { PathPrefix = "src/\ud83d" },
            new() { ExcludeGlobs = ["src/0008.txt"], PathRegex = "[.]txt\\z" }
        })
        {
            var query = new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive)
                { MaxHits = int.MaxValue, MaxHitsPerDocument = 1 };
            var reference = new HitBuffer();
            snapshot.Search(query, remainder, reference);
            var expected = reference.Hits.Select(hit => snapshot.GetDocumentKey(hit).Path).Where(accepted.Contains).ToArray();
            var filter = new PathFilter(remainder.PathPrefix, includeScope, remainder.ExcludeGlobs, remainder.PathRegex);
            var actual = new HitBuffer();
            snapshot.Search(query, filter, actual);
            Assert.Equal(expected, actual.Hits.Select(hit => snapshot.GetDocumentKey(hit).Path));
            var limited = new HitBuffer();
            snapshot.Search(query with { MaxHits = 3 }, filter, limited);
            Assert.Equal(expected.Take(3), limited.Hits.Select(hit => snapshot.GetDocumentKey(hit).Path));
        }
    }
}
