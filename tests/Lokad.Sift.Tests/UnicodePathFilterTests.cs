using System.Text.RegularExpressions;

namespace Lokad.Sift.Tests;

public sealed class UnicodePathFilterTests
{
    private static readonly string[] Paths = ["😀.txt", "�.txt", "prefix/😀.txt", "prefix/�.txt",
        "plain.txt", "café.txt", "😀/leaf.txt", "�/leaf.txt", "prefix/😀/leaf.txt", "prefix/�/leaf.txt"];

    private static readonly (string Name, PathFilter Filter, bool UsesPathIndex)[] Filters =
    [
        ("prefix", new() { PathPrefix = "\ud83d" }, false),
        ("prefix-with-glob", new() { PathPrefix = "\ud83d", IncludeGlobs = ["*"] }, false),
        ("low-prefix", new() { PathPrefix = "\ude00", IncludeGlobs = ["*"] }, false),
        ("include-prefix", new() { IncludeGlobs = ["\ud83d*"] }, false),
        ("include-suffix", new() { IncludeGlobs = ["*\ude00.txt"] }, false),
        ("include-exact", new() { IncludeGlobs = ["\ud83d.txt"] }, false),
        ("include-recursive-prefix", new() { IncludeGlobs = ["prefix/\ud83d/**"] }, false),
        ("include-recursive-suffix", new() { IncludeGlobs = ["\ud83d**/leaf.txt"] }, true),
        ("include-question-mark", new() { IncludeGlobs = ["prefix/?\ude00.txt"] }, true),
        ("include-index-fragment", new() { IncludeGlobs = ["prefix/\ud83d*"] }, false),
        ("include-alternatives", new() { IncludeGlobs = ["\ud83d*", "plain.txt"] }, false),
        ("exclude-prefix", new() { ExcludeGlobs = ["\ud83d*"] }, false),
        ("exclude-suffix", new() { ExcludeGlobs = ["*\ude00.txt"] }, false),
        ("exclude-exact", new() { ExcludeGlobs = ["\ud83d.txt"] }, false),
        ("exclude-recursive", new() { ExcludeGlobs = ["\ud83d**/leaf.txt"] }, false),
        ("combined", new() { PathPrefix = "prefix/", IncludeGlobs = ["prefix/*"], ExcludeGlobs = ["prefix/\ud83d*"] }, true),
        ("regex-index-hint", new() { PathRegex = "^prefix/", IncludeGlobs = ["prefix/\ud83d*"] }, true),
        ("valid-glob-fragment", new() { IncludeGlobs = ["*\ud83d*.txt"] }, true),
        ("valid-unicode-prefix", new() { PathPrefix = "😀", IncludeGlobs = ["*"] }, false),
        ("valid-unicode-glob", new() { IncludeGlobs = ["prefix/😀*"] }, true),
        ("replacement-prefix", new() { PathPrefix = "�", IncludeGlobs = ["*"] }, false),
        ("replacement-glob", new() { IncludeGlobs = ["prefix/�*"] }, true),
        ("bmp-control", new() { IncludeGlobs = ["café*"] }, true)
    ];

    public static IEnumerable<object[]> RoutesAndFilters()
    {
        foreach (var route in new[] { "snapshot", "parallel", "overlay-base", "overlay-own" })
        foreach (var (name, _, _) in Filters)
            yield return [route, name];
    }

    [Theory]
    [MemberData(nameof(RoutesAndFilters))]
    public async Task AcceptedUtf16FiltersHaveTheSameMeaningAcrossVerificationRoutes(string route, string name)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { TargetSegmentDocumentCount = 1 });
        await index.Build(Source(Paths));
        using IIndexSnapshot snapshot = route.StartsWith("overlay", StringComparison.Ordinal)
            ? index.OpenOverlaySnapshot(new SearchOptions(2))
            : index.OpenSnapshot(new SearchOptions(route == "parallel" ? 2 : 1));
        if (route == "overlay-own") await ((IOverlaySnapshot)snapshot).Update(Source(Paths, "updated needle"), []);
        var (_, original, usesPathIndex) = Filters.Single(filter => filter.Name == name);

        foreach (var pattern in new[] { "", "needle" })
        foreach (var pathRegex in new[] { original.PathRegex, original.PathRegex ?? ".*" }.Distinct())
        {
            var filter = new PathFilter(original.PathPrefix, original.IncludeGlobs, original.ExcludeGlobs, pathRegex);
            var expected = ExpectedPaths(Paths, filter);
            var query = new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive);

            AssertPaths(snapshot, query, filter, expected);

            var plan = snapshot.Explain(query, filter);
            // Overlay diagnostics describe physical scopes before base visibility filtering.
            Assert.Equal(expected.Length * (route == "overlay-own" ? 2 : 1), plan.ScopeDocumentCount);
            Assert.Equal(usesPathIndex, plan.UsesPathIndex);
        }
    }

    [Theory]
    [InlineData("prefix-with-glob")]
    [InlineData("include-index-fragment")]
    [InlineData("include-suffix")]
    public async Task FiltersSurviveUpdateCompactionAndReopeningWithRetainedSnapshots(string name)
    {
        using var fixture = new CorpusFixture();
        var filter = Filters.Single(filter => filter.Name == name).Filter;
        var query = new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive);
        var currentPaths = Paths.Where(path => path != "prefix/😀.txt").Append("😀-new.txt").ToArray();
        using (var index = new SiftIndex(fixture.CorpusPath, new SiftOptions { TargetSegmentDocumentCount = 1 }))
        {
            await index.Build(Source(Paths));
            using var retained = index.OpenSnapshot();
            await index.Update(Source(["😀-new.txt"]), [new DocumentKey("prefix/😀.txt")]);
            await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
            using var current = index.OpenSnapshot();

            AssertPaths(retained, query, filter, ExpectedPaths(Paths, filter));
            AssertPaths(current, query, filter, ExpectedPaths(currentPaths, filter));
            Assert.False(index.RequiresRebuild);
        }
        using var reopened = new SiftIndex(fixture.CorpusPath);
        using var snapshot = reopened.OpenSnapshot();
        AssertPaths(snapshot, query, filter, ExpectedPaths(currentPaths, filter));
    }

    private static InMemoryDocumentSource Source(IEnumerable<string> paths, string content = "needle") =>
        new(paths.Select(path => (path, content)).ToArray());

    private static void AssertPaths(IIndexSnapshot snapshot, SearchQuery query, PathFilter filter, string[] expected)
    {
        var hits = new HitBuffer();
        var stats = snapshot.Search(query, filter, hits);
        Assert.Equal(expected.Length, stats.HitsReturned);
        Assert.Equal(expected, hits.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).Path).Order(StringComparer.Ordinal));
    }

    private static string[] ExpectedPaths(IEnumerable<string> paths, PathFilter filter) => paths.Where(path =>
        (filter.PathPrefix is null || path.StartsWith(filter.PathPrefix, StringComparison.Ordinal)) &&
        (filter.PathRegex is null || Regex.IsMatch(path, filter.PathRegex, RegexOptions.CultureInvariant)) &&
        (filter.IncludeGlobs.Count == 0 || filter.IncludeGlobs.Any(glob => MatchesGlob(path, glob))) &&
        !filter.ExcludeGlobs.Any(glob => MatchesGlob(path, glob))).Order(StringComparer.Ordinal).ToArray();

    private static bool MatchesGlob(string path, string glob) => Regex.IsMatch(path,
        @"\A" + Regex.Escape(glob).Replace(@"\*\*", @"[\s\S]*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + @"\z",
        RegexOptions.CultureInvariant);
}
