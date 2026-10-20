namespace Lokad.Sift.Tests;

public sealed class PathGlobTests
{
    [Theory]
    [InlineData("src/file?.txt", "src/file1.txt\n", false)]
    [InlineData("src/file?.txt", "src/file1.txt", true)]
    [InlineData("src/file?.txt", "\nsrc/file1.txt", false)]
    [InlineData("src/file?.txt", "src/file\n.txt", true)]
    [InlineData("src/*file*.txt", "src/file1.txt\n", false)]
    [InlineData("src/**/file?.txt", "src/a\nb/file1.txt", true)]
    [InlineData("src/**/file1.txt", "src/a\nb/file1.txt", true)]
    [InlineData("src/**", "src/a\nb/file1.txt", true)]
    [InlineData("src/**/file?.txt", "src/a\r\nb/file1.txt", true)]
    [InlineData("src/**/file?.txt", "src/file1.txt", false)]
    [InlineData("src/*/file?.txt", "src/a\nb/file1.txt", true)]
    [InlineData("src/*/file?.txt", "src/a/b/file1.txt", false)]
    [InlineData("src/file?.txt", "src/file1.txt\r", false)]
    [InlineData("src/*file*.txt", "src/file1\nmore.txt", true)]
    [InlineData("src/[draft]?+*.txt", "src/[draft]1+note\ns.txt", true)]
    [InlineData("src/file?.txt\n", "src/file1.txt\n", true)]
    public async Task IncludeAndExcludeGlobsMatchCompletePaths(string glob, string path, bool expected)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([(path, "needle")]));
        using var snapshot = index.OpenSnapshot();
        foreach (var pattern in new[] { "", "needle" })
        foreach (var (filter, accepted, usesPathIndex) in new[]
        {
            (new PathFilter { IncludeGlobs = [glob] }, expected, true),
            (new PathFilter { IncludeGlobs = [glob, "elsewhere/**"] }, expected, false),
            (new PathFilter { ExcludeGlobs = [glob] }, !expected, false)
        })
        {
            var query = new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive);
            var hits = new HitBuffer();

            snapshot.Search(query, filter, hits);

            Assert.Equal(accepted ? new[] { path } : [],
                hits.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).Path));
            Assert.Equal(usesPathIndex, snapshot.Explain(query, filter).UsesPathIndex);
        }
    }

    [Theory]
    [InlineData("src/file?.txt", "src/file1.txt\n", false)]
    [InlineData("src/**/file?.txt", "src/a\nb/file1.txt", true)]
    public async Task OverlayFiltersUseTheSameWholePathSemantics(string glob, string path, bool expected)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { TargetSegmentDocumentCount = 1 });
        await index.Build(new InMemoryDocumentSource([("src/stale.txt", "needle")]));
        using var overlay = index.OpenOverlaySnapshot(new SearchOptions(2));
        await overlay.Update(new InMemoryDocumentSource([(path, "needle")]), [new DocumentKey("src/stale.txt")]);
        var hits = new HitBuffer();

        overlay.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { IncludeGlobs = [glob] }, hits);

        Assert.Equal(expected ? new[] { path } : [], hits.Hits.Select(hit => overlay.Materialize(hit, 0, 0).Path));
    }

    [Fact]
    public async Task PathRegexRetainsManagedDollarAnchorSemantics()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/file1.txt\n", "needle")]));
        using var snapshot = index.OpenSnapshot();

        var stats = snapshot.Search(new SearchQuery("", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathRegex = @"^src/file[0-9]\.txt$" }, new HitBuffer());

        Assert.Equal(1, stats.HitsReturned);
    }
}
