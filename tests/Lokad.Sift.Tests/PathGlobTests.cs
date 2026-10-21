namespace Lokad.Sift.Tests;

public sealed class PathGlobTests
{
    [Theory]
    [InlineData("snapshot", false)]
    [InlineData("snapshot", true)]
    [InlineData("parallel", false)]
    [InlineData("parallel", true)]
    [InlineData("overlay", false)]
    [InlineData("overlay", true)]
    public async Task RecursiveGlobsRespectLiteralBoundariesAndWildcardPrefixes(string route, bool stringVerification)
    {
        string[] paths = ["dir/File.cs", "dir/NotFile.cs", "dir/deep/File.cs", "dir/deep/NotFile.cs",
            "dir/File.cs/OtherFile.cs", "src/a/Widget.cs", "src/a/MyWidget.cs", "src/a/Widget.cs/MyWidget.cs",
            "src/Widget.cs", "Src/File.cs", "Src/deep/MyFile.cs", "Src/a/File.cs", "ab/cd", "ab/xxcd",
            "ab/cd/xxcd", "é/NotFile.cs", "é/File.cs", "😀/File.cs", "src/a\nb/Widget.cs", "a/a", "a/b/a"];
        (string Glob, string[] Accepted)[] cases =
        [
            ("**/File.cs", ["dir/File.cs", "dir/deep/File.cs", "Src/File.cs", "Src/a/File.cs", "é/File.cs", "😀/File.cs"]),
            ("src/**/Widget.cs", ["src/a/Widget.cs", "src/a\nb/Widget.cs"]),
            ("ab**/cd", ["ab/cd"]),
            ("dir*/**/File.cs", ["dir/deep/File.cs"]),
            ("src*/**/Widget.cs", ["src/a/Widget.cs", "src/a\nb/Widget.cs"]),
            ("a/**/a", ["a/b/a"]),
            ("é**/File.cs", ["é/File.cs"]),
            ("dir/*.cs", ["dir/File.cs", "dir/NotFile.cs"]),
            ("src/**", ["src/a/Widget.cs", "src/a/MyWidget.cs", "src/a/Widget.cs/MyWidget.cs", "src/Widget.cs", "src/a\nb/Widget.cs"])
        ];
        using var index = SiftIndex.CreateInMemory(new SiftOptions { TargetSegmentDocumentCount = 1 });
        await index.Build(new InMemoryDocumentSource(paths.Select(path => (path, route == "overlay" ? "stale" : "needle")).ToArray()));
        using IIndexSnapshot snapshot = route == "overlay" ? index.OpenOverlaySnapshot(new SearchOptions(2)) :
            index.OpenSnapshot(new SearchOptions(route == "parallel" ? 2 : 1));
        if (snapshot is IOverlaySnapshot overlay)
            await overlay.Update(new InMemoryDocumentSource(paths.Select(path => (path, "needle")).ToArray()), []);

        foreach (var (glob, accepted) in cases)
        foreach (var mode in new[] { "single", "alternative", "exclude" })
        foreach (var pattern in new[] { "", "needle" })
        {
            var filter = new PathFilter
            {
                IncludeGlobs = mode == "single" ? [glob] : mode == "alternative" ? [glob, "never-match"] : [],
                ExcludeGlobs = mode == "exclude" ? [glob] : [],
                PathRegex = stringVerification ? ".*" : null
            };
            var expected = mode == "exclude" ? paths.Except(accepted) : accepted;
            var hits = new HitBuffer();
            snapshot.Search(new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive), filter, hits);
            var actual = hits.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).Path);
            Assert.True(expected.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal)),
                $"{route}: {glob}, {mode}, string={stringVerification}, empty={pattern.Length == 0}");
        }
    }

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
