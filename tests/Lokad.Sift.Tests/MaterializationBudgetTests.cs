using Lokad.Sift;

namespace Lokad.Sift.Tests;

public sealed class MaterializationBudgetTests
{
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 1)]
    public async Task LargeContextBudgetsMaterializeBothDirections(int budget)
    {
        foreach (var unicode in new[] { false, true })
        {
            var before = unicode ? "avant é" : "before";
            var after = unicode ? "après é" : "after";
            using var index = SiftIndex.CreateInMemory(new SiftOptions { MaxMaterializedContextBytes = budget });
            await index.Build(new InMemoryDocumentSource([("a.txt", $"prefix\n{before}\nalpha\n{after}\nlast")]));
            using var snapshot = index.OpenSnapshot();
            var hits = new HitBuffer();
            snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), hits);
            var raw = Assert.Single(hits.Hits);

            var hit = snapshot.Materialize(raw, 1, 1);
            var withLine = snapshot.MaterializeWithMatchingLine(raw, 1, 1);

            Assert.Equal(new[] { before }, hit.BeforeContext);
            Assert.Equal(new[] { after }, hit.AfterContext);
            Assert.Equal(hit.BeforeContext, withLine.Hit.BeforeContext);
            Assert.Equal(hit.AfterContext, withLine.Hit.AfterContext);
            Assert.Equal("alpha", withLine.MatchingLine);
            Assert.False(hit.IsContextTruncated);
            Assert.False(withLine.Hit.IsContextTruncated);
        }
    }

    [Theory]
    [InlineData(0, 2, null, null, true)]
    [InlineData(1, 2, "", "", true)]
    [InlineData(2, 2, "é", null, true)]
    [InlineData(3, 2, "é", "", true)]
    [InlineData(5, 2, "éé", null, true)]
    [InlineData(7, 2, "éé", "é", true)]
    [InlineData(9, 2, "éé", "éé", false)]
    [InlineData(int.MaxValue, 0, null, null, true)]
    [InlineData(int.MaxValue, 1, "éé", null, true)]
    public async Task ContextBudgetsAreSharedAndPreserveUtf8Scalars(
        int byteBudget, int lineBudget, string? before, string? after, bool truncated)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            MaxMaterializedContextBytes = byteBudget,
            MaxMaterializedContextLines = lineBudget
        });
        await index.Build(new InMemoryDocumentSource([("a.txt", "éé\nalpha\néé")]));
        using var snapshot = index.OpenSnapshot();
        var hits = new HitBuffer();
        snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), hits);
        var raw = Assert.Single(hits.Hits);

        var hit = snapshot.Materialize(raw, 1, 1);
        var withLine = snapshot.MaterializeWithMatchingLine(raw, 1, 1);

        Assert.Equal(before is null ? [] : new[] { before }, hit.BeforeContext);
        Assert.Equal(after is null ? [] : new[] { after }, hit.AfterContext);
        Assert.Equal(truncated, hit.IsContextTruncated);
        Assert.Equal(hit.BeforeContext, withLine.Hit.BeforeContext);
        Assert.Equal(hit.AfterContext, withLine.Hit.AfterContext);
        Assert.Equal(hit.IsContextTruncated, withLine.Hit.IsContextTruncated);
        Assert.Equal("alpha", withLine.MatchingLine);
        Assert.Equal("alpha", hit.MatchText);
        Assert.Equal(2, hit.StartLine);
        Assert.Equal(2, hit.EndLine);
        Assert.Equal(1, hit.StartColumn);
        Assert.Equal(6, hit.EndColumn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task EmptyDocumentHasNoContextToTruncate(int budget)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { MaxMaterializedContextBytes = budget });
        await index.Build(new InMemoryDocumentSource([("empty.txt", "")]));
        using var snapshot = index.OpenSnapshot();
        var hits = new HitBuffer();
        snapshot.Search(new SearchQuery("", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), hits);
        var raw = Assert.Single(hits.Hits);

        var hit = snapshot.Materialize(raw, 5, 5);
        var withLine = snapshot.MaterializeWithMatchingLine(raw, 5, 5);

        Assert.Empty(hit.BeforeContext);
        Assert.Empty(hit.AfterContext);
        Assert.Empty(hit.MatchText);
        Assert.Empty(withLine.MatchingLine);
        Assert.Empty(withLine.Hit.BeforeContext);
        Assert.Empty(withLine.Hit.AfterContext);
        Assert.False(hit.IsContextTruncated);
        Assert.False(withLine.Hit.IsContextTruncated);
        Assert.Equal(1, hit.StartLine);
        Assert.Equal(1, hit.EndLine);
        Assert.Equal(1, hit.StartColumn);
        Assert.Equal(1, hit.EndColumn);
    }
}
