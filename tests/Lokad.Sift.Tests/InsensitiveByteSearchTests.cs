using System.Text;

namespace Lokad.Sift.Tests;

public sealed class InsensitiveByteSearchTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("aBa")]
    [InlineData("aBcDeFgHi")]
    [InlineData("abcabcab")]
    [InlineData("a#b")]
    [InlineData("@")]
    [InlineData("[x]")]
    public async Task MatchesRetainByteOffsetsAcrossTailsAndUnicodeSurroundings(string pattern)
    {
        var documents = new List<(string Path, string Content)>();
        foreach (var prefixLength in new[] { 0, 1, 15, 31, 32, 33, 63, 64, 65, 4095, 4096, 4097 })
        foreach (var suffixLength in new[] { 0, 1, 31, 32 })
        foreach (var unicode in new[] { false, true })
        {
            var prefix = new string('|', prefixLength) + (unicode ? "é😀\n" : "");
            documents.Add(($"doc-{documents.Count:D3}.txt",
                prefix + pattern.ToUpperInvariant() + new string('|', suffixLength)));
        }
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource(documents));
        using var snapshot = index.OpenSnapshot();
        var hits = new HitBuffer();

        snapshot.Search(new SearchQuery(pattern, PatternMode.Literal, CaseMode.Insensitive), new(), hits);

        Assert.Equal(documents.Count, hits.Hits.Count);
        var content = documents.ToDictionary(document => document.Path, document => document.Content);
        var visited = new HashSet<string>();
        foreach (var hit in hits.Hits)
        {
            var path = snapshot.GetDocumentKey(hit).Path;
            Assert.True(visited.Add(path));
            var text = content[path];
            var start = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            var startByte = Encoding.UTF8.GetByteCount(text.AsSpan(0, start));
            Assert.Equal((startByte, startByte + Encoding.UTF8.GetByteCount(pattern)), (hit.StartByte, hit.EndByte));
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(17)]
    [InlineData(2047)]
    public async Task DenseNearMatchesDoNotHideLaterMatches(int length)
    {
        var pattern = new string('a', length - 2) + "ba";
        var text = new string('a', 16384) + pattern.ToUpperInvariant();
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", text)]));
        using var snapshot = index.OpenSnapshot();
        var hits = new HitBuffer();

        snapshot.Search(new SearchQuery(pattern, PatternMode.Literal, CaseMode.Insensitive), new(), hits);

        var hit = Assert.Single(hits.Hits);
        Assert.Equal((16384, text.Length), (hit.StartByte, hit.EndByte));
    }

    [Fact]
    public async Task PeriodicMatchesKeepOverlapOrderAndHitLimits()
    {
        var text = string.Concat(Enumerable.Repeat("ABABA", 1024));
        const string pattern = "aba";
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", text)]));
        using var snapshot = index.OpenSnapshot();
        var hits = new HitBuffer();
        var query = new SearchQuery(pattern, PatternMode.Literal, CaseMode.Insensitive) { MaxHits = 513 };
        var expected = new List<int>();
        for (var offset = 0; expected.Count < query.MaxHits;)
        {
            var found = text.IndexOf(pattern, offset, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            expected.Add(found);
            offset = found + 1;
        }

        var stats = snapshot.Search(query, new(), hits);

        Assert.Equal(expected, hits.Hits.Select(hit => hit.StartByte));
        Assert.All(hits.Hits, hit => Assert.Equal(pattern.Length, hit.EndByte - hit.StartByte));
        Assert.Equal(SearchStopReason.MaxHits, stats.StopReason);
    }
}
