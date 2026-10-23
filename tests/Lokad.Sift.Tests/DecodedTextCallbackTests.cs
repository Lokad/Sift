using System.Buffers;

namespace Lokad.Sift.Tests;

public sealed class DecodedTextCallbackTests
{
    [Theory]
    [InlineData(PatternMode.Regex, 1, int.MaxValue)]
    [InlineData(PatternMode.Regex, 2, int.MaxValue)]
    [InlineData(PatternMode.Regex, int.MaxValue, int.MaxValue)]
    [InlineData(PatternMode.Regex, int.MaxValue, 1)]
    [InlineData(PatternMode.Literal, 1, int.MaxValue)]
    [InlineData(PatternMode.Literal, 2, int.MaxValue)]
    [InlineData(PatternMode.Literal, int.MaxValue, int.MaxValue)]
    [InlineData(PatternMode.Literal, int.MaxValue, 1)]
    public async Task NestedSearchAndPooledWorkspacePreserveDecodedHits(PatternMode mode, int perDocumentLimit, int totalLimit)
    {
        var text = new string('é', 2048) + (mode == PatternMode.Regex
            ? "\nitem1\nitem2\nitem3"
            : "\ncafé\ncafé\ncafé");
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", text)]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery(mode == PatternMode.Regex ? @"item\d" : "CAFÉ", mode,
            mode == PatternMode.Regex ? CaseMode.Sensitive : CaseMode.Insensitive)
        {
            MaxHitsPerDocument = perDocumentLimit,
            MaxHits = totalLimit
        };
        var outer = new List<RawHit>();
        var collector = new CallbackCollector(hit =>
        {
            var scratch = ArrayPool<char>.Shared.Rent(text.Length);
            try
            {
                scratch.AsSpan().Fill('!');
                var nested = new HitBuffer();
                var stats = snapshot.Search(query with { MaxHitsPerDocument = 1 }, new(), nested);
                Assert.Equal(1, stats.HitsReturned);
                Assert.Equal(mode == PatternMode.Regex ? "item1" : "café", snapshot.Materialize(Assert.Single(nested.Hits), 0, 0).MatchText);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(scratch);
            }
            outer.Add(hit);
            return true;
        });

        var result = snapshot.Search(query, new(), collector);

        var expected = Math.Min(3, Math.Min(perDocumentLimit, totalLimit));
        Assert.Equal(expected, result.HitsReturned);
        if (mode == PatternMode.Regex) Assert.True(result.FallbackRegexInvocations > 0);
        Assert.True(result.DecodedChars > 0);
        Assert.Equal(Enumerable.Range(1, expected).Select(i => mode == PatternMode.Regex ? $"item{i}" : "café"),
            outer.Select(hit => snapshot.Materialize(hit, 0, 0).MatchText));
        Assert.Equal(Enumerable.Range(2, expected), outer.Select(hit => hit.StartLine));
    }

    private sealed class CallbackCollector(Func<RawHit, bool> callback) : IHitCollector
    {
        public bool OnHit(in RawHit hit) => callback(hit);
    }
}
