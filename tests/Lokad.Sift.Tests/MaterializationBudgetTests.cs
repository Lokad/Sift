using Lokad.Sift;

namespace Lokad.Sift.Tests;

public sealed class MaterializationBudgetTests
{
    [Theory]
    [InlineData("before\rXYZ\n", 7, "before\r", true)]
    [InlineData("before\r\rXYZ\n", 8, "before\r\r", true)]
    [InlineData("é\rXYZ\n", 3, "é\r", true)]
    [InlineData("é\réXYZ\n", 4, "é\r", true)]
    [InlineData("before\rXYZ\n", 11, "before\rXYZ", false)]
    [InlineData("before\r\n", 6, "before", true)]
    [InlineData("before\r\n", 7, "before", true)]
    [InlineData("before\r\n", 8, "before", false)]
    [InlineData("before\r\r\n", 7, "before\r", true)]
    [InlineData("before\r\r\n", 8, "before\r", true)]
    [InlineData("before\r\r\n", 9, "before\r", false)]
    [InlineData("é\r\r\n", 3, "é\r", true)]
    [InlineData("é\r\r\n", 5, "é\r", false)]
    public async Task ContextTrimmingUsesTheCompleteLine(
        string context, int budget, string expected, bool truncated)
    {
        foreach (var before in new[] { false, true })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions { MaxMaterializedContextBytes = budget });
            var content = before ? context + "needle\n" : "needle\n" + context;
            await index.Build(new InMemoryDocumentSource([("a.txt", content)]));
            using var snapshot = index.OpenSnapshot();
            var hits = new HitBuffer();
            snapshot.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new(), hits);
            var raw = Assert.Single(hits.Hits);

            var hit = snapshot.Materialize(raw, before ? 1 : 0, before ? 0 : 1);
            var withLine = snapshot.MaterializeWithMatchingLine(raw, before ? 1 : 0, before ? 0 : 1);

            Assert.Equal(new[] { expected }, before ? hit.BeforeContext : hit.AfterContext);
            Assert.Empty(before ? hit.AfterContext : hit.BeforeContext);
            Assert.Equal(hit.BeforeContext, withLine.Hit.BeforeContext);
            Assert.Equal(hit.AfterContext, withLine.Hit.AfterContext);
            Assert.Equal(truncated, hit.IsContextTruncated);
            Assert.Equal(truncated, withLine.Hit.IsContextTruncated);
            Assert.Equal("needle", withLine.MatchingLine);
            Assert.Equal("needle", hit.MatchText);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingLinesAndContextPreserveLiteralCarriageReturns(bool disk)
    {
        (string Ending, string LiteralSuffix)[] cases =
        [
            ("", ""), ("\n", ""), ("\r", ""), ("\r\n", ""),
            ("\r\r", "\r"), ("\r\r\n", "\r"),
            ("\r\r\r", "\r\r"), ("\r\r\r\n", "\r\r")
        ];
        foreach (var stem in new[] { "needle", "café", "😀" })
        foreach (var route in new[] { "snapshot", "overlay-base", "overlay-delta" })
        {
            using var fixture = new CorpusFixture();
            var options = new SiftOptions { TargetSegmentDocumentCount = 1, MinimumFreeBytesAfterMutation = 0 };
            using var index = disk ? new SiftIndex(fixture.CorpusPath, options) : SiftIndex.CreateInMemory(options);
            var documents = cases.Select((item, i) => ($"doc-{i}.txt", "before\r\r\n" + stem + item.Ending +
                (item.Ending.EndsWith('\n') ? "after\r\r\n" : ""))).ToArray();
            await index.Build(new InMemoryDocumentSource(route == "overlay-delta"
                ? documents.Select(document => (document.Item1, "stale")).ToArray() : documents));
            using IIndexSnapshot snapshot = route == "snapshot"
                ? index.OpenSnapshot() : index.OpenOverlaySnapshot();
            if (route == "overlay-delta")
                await ((IOverlaySnapshot)snapshot).Update(new InMemoryDocumentSource(documents), []);

            for (var i = 0; i < cases.Length; i++)
            {
                var expected = stem + cases[i].LiteralSuffix;
                var hits = new HitBuffer();
                snapshot.Search(new SearchQuery(expected, PatternMode.Literal, CaseMode.Sensitive) { WholeLine = true },
                    new PathFilter { PathPrefix = documents[i].Item1 }, hits);
                var raw = Assert.Single(hits.Hits);
                var hit = snapshot.Materialize(raw, 1, 1);
                var withLine = snapshot.MaterializeWithMatchingLine(raw, 1, 1);

                Assert.Equal(expected, hit.MatchText);
                Assert.Equal(expected, withLine.MatchingLine);
                Assert.Equal(expected, withLine.MatchingLine[(hit.StartColumn - 1)..(hit.EndColumn - 1)]);
                Assert.Equal((2, 1, 2, expected.Length + 1), (hit.StartLine, hit.StartColumn, hit.EndLine, hit.EndColumn));
                Assert.Equal(new[] { "before\r" }, hit.BeforeContext);
                Assert.Equal(cases[i].Ending.EndsWith('\n') ? new[] { "after\r" } : [], hit.AfterContext);
                Assert.Equal(hit.MatchText, withLine.Hit.MatchText);
                Assert.Equal(hit.BeforeContext, withLine.Hit.BeforeContext);
                Assert.Equal(hit.AfterContext, withLine.Hit.AfterContext);
                Assert.False(hit.IsMatchTruncated);
                Assert.False(hit.IsContextTruncated);
            }
        }
    }

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
