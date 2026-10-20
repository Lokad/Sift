using Lokad.Sift.Search;

namespace Lokad.Sift.Tests;

public sealed class PathMatcherTests
{
    [Theory]
    [InlineData("src/generated/**", "src/generated/a.txt", true)]
    [InlineData("src/generated/**", "src/generated/nested/a.txt", true)]
    [InlineData("src/generated/**", "src/other/a.txt", false)]
    [InlineData("src/*.txt", "src/a.txt", true)]
    [InlineData("src/*.txt", "src/nested/a.txt", false)]
    [InlineData("src/**/*.txt", "src/nested/a.txt", true)]
    [InlineData("src/**/*.txt", "src/nested/deep/a.txt", true)]
    [InlineData("src/**/*.txt", "src/a.txt", false)]
    [InlineData("docs/config/*.txt", "docs/config/a.txt", true)]
    [InlineData("docs/config/*.txt", "docs/config/nested/a.txt", false)]
    [InlineData("docs/readme.md", "docs/readme.md", true)]
    [InlineData("docs/readme.md", "docs/README.md", false)]
    public async Task GlobMatcherPreservesExpectedSemantics(string glob, string path, bool expected)
    {
        var matcher = GlobMatcher.Create(glob);
        Assert.Equal(expected, matcher.IsMatch(path));
    }

    [Fact]
    public async Task GlobMatcherFallsBackToRegexForQuestionMarkPatterns()
    {
        var matcher = GlobMatcher.Create("src/file?.txt");

        Assert.True(matcher.IsMatch("src/file1.txt"));
        Assert.False(matcher.IsMatch("src/file10.txt"));
    }

    [Fact]
    public async Task RegexGlobEscapesMetacharacters()
    {
        var matcher = GlobMatcher.Create("src/[draft]?+*.txt");

        Assert.True(matcher.IsMatch("src/[draft]1+notes.txt"));
        Assert.False(matcher.IsMatch("src/draft1+notes.txt"));
    }

    [Fact]
    public async Task PathRegexAccelerationPreservesContentMatches()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/special-file.txt", "alpha---beta"),
            ("src/special-file.other.txt", "unrelated"), ("src/other.txt", "alpha---beta")]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("alpha.*beta", PatternMode.Regex, CaseMode.Sensitive);
        var filter = new PathFilter { PathRegex = "special-file" };
        var hits = new HitBuffer();

        snapshot.Search(query, filter, hits);
        var plan = snapshot.Explain(query, filter);

        Assert.Equal("src/special-file.txt", snapshot.Materialize(Assert.Single(hits.Hits), 0, 0).Path);
        Assert.True(plan.UsesPathIndex);
        Assert.Equal(new[] { "alpha", "beta" }, plan.MandatoryFragments);
    }
}



