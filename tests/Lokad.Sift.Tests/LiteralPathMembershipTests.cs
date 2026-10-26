namespace Lokad.Sift.Tests;

public sealed class LiteralPathMembershipTests
{
    [Theory]
    [InlineData(16, false)]
    [InlineData(16, true)]
    [InlineData(512, false)]
    [InlineData(512, true)]
    public async Task LiteralAlternativesMatchOrdinalMembershipInBothVerificationRoutes(int missingPaths, bool pathRegex)
    {
        var paths = Enumerable.Range(0, 96).Select(n =>
            $"src/{(n % 3 == 0 ? "𐀀" : n % 3 == 1 ? "\ue000" : "café")}/{n:D4}.txt").ToArray();
        var accepted = paths.Where((_, n) => n % 5 == 0).ToHashSet(StringComparer.Ordinal);
        var excluded = accepted.First();
        var includes = accepted.Reverse().Concat(Enumerable.Range(0, missingPaths).Select(n => $"absent/{n:D4}.txt"))
            .Append(accepted.First().Replace('/', '\\')).ToArray();
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            MaxPathFilterPatterns = 1024,
            MaxPathFilterCharacters = 32 * 1024
        });
        await index.Build(new InMemoryDocumentSource(paths.Select(path => (path, "needle")).ToArray()));
        using var snapshot = index.OpenSnapshot();

        foreach (var pattern in new[] { "", "needle" })
        foreach (var mixed in new[] { false, true })
        {
            var filter = new PathFilter
            {
                IncludeGlobs = mixed ? [.. includes, "never-match/**"] : includes,
                ExcludeGlobs = [excluded],
                PathRegex = pathRegex ? "[.]txt\\z" : null
            };
            var hits = new HitBuffer();
            snapshot.Search(new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive)
                { MaxHits = int.MaxValue, MaxHitsPerDocument = 1 }, filter, hits);
            Assert.Equal(accepted.Where(path => path != excluded).Order(StringComparer.Ordinal),
                hits.Hits.Select(hit => snapshot.GetDocumentKey(hit).Path));
        }
    }
}
