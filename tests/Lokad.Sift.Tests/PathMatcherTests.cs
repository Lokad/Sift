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
    public async Task PathRegexAccelerationPrunesContainedMandatoryFragments()
    {
        var matcher = new PathFilterMatcher(new Lokad.Sift.PathFilter { PathRegex = "special-file" });

        Assert.NotEmpty(matcher.GetPathTrigrams());
    }
}



