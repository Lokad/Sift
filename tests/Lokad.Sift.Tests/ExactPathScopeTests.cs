namespace Lokad.Sift.Tests;

public sealed class ExactPathScopeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(4096)]
    public async Task LiteralAlternativesPreserveVisibleResultsAndNaturalOrder(int segmentSize)
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath,
            new SiftOptions { TargetSegmentDocumentCount = segmentSize });
        string[] selected = ["src/a.txt", "src/café.txt", "src/😀.txt", "src/\ue000.txt", "src/new.txt"];
        var original = Enumerable.Range(0, 128).Select(n => ($"src/filler-{n:D4}.txt", "needle"))
            .Concat(selected[..^1].Append("src/a.txt\n").Select(path => (path, "needle"))).ToArray();
        await index.Build(new InMemoryDocumentSource(original));
        using var retained = index.OpenSnapshot();
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new InMemoryDocumentSource([("src/new.txt", "needle"), ("src/a.txt", "needle updated")]),
            [new DocumentKey("src/café.txt")]);
        await index.Update(new InMemoryDocumentSource([("src/new.txt", "needle"), ("src/a.txt", "needle updated")]),
            [new DocumentKey("src/café.txt")]);
        await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
        using var current = index.OpenSnapshot();

        foreach (var snapshot in new IIndexSnapshot[] { retained, overlay, current })
        foreach (var pattern in new[] { "", "needle" })
        foreach (var remainder in new PathFilter[]
        {
            new(),
            new() { PathPrefix = "src/a" },
            new() { PathPrefix = "absent/" },
            new() { ExcludeGlobs = ["src/😀.txt"] },
            new() { PathPrefix = "src/\ud83d" },
            new() { PathPrefix = "src/", ExcludeGlobs = ["src/😀.txt"], PathRegex = "[.]txt\\z" }
        })
        {
            var chosen = selected.ToHashSet(StringComparer.Ordinal);
            var query = new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive)
                { MaxHits = int.MaxValue, MaxHitsPerDocument = 1 };
            var all = new HitBuffer();
            snapshot.Search(query, remainder, all);
            var expected = all.Hits.Select(hit => snapshot.GetDocumentKey(hit).Path).Where(chosen.Contains).ToArray();
            var filter = new PathFilter(remainder.PathPrefix,
                ["src\\😀.txt", "src/a.txt", "src/café.txt", "src/\ue000.txt", "src/new.txt", "missing.txt", "src/a.txt", " "],
                remainder.ExcludeGlobs, remainder.PathRegex);
            var hits = new HitBuffer();
            snapshot.Search(query, filter, hits);
            Assert.Equal(expected, hits.Hits.Select(hit => snapshot.GetDocumentKey(hit).Path));

            var limited = new HitBuffer();
            snapshot.Search(query with { MaxHits = 2 }, filter, limited);
            Assert.Equal(expected.Take(2), limited.Hits.Select(hit => snapshot.GetDocumentKey(hit).Path));
        }
    }
}
