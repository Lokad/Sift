using System.Text.RegularExpressions;

namespace Lokad.Sift.Tests;

public sealed class CollectorFailureTests
{
    public static IEnumerable<object[]> RoutesAndPolicies()
    {
        foreach (var mode in new[] { "snapshot", "parallel", "overlay", "overlay-base",
            "native", "native-parallel", "native-overlay" })
        foreach (var policy in new[] { RegexTimeoutPolicy.StopSearch, RegexTimeoutPolicy.Throw })
            yield return [mode, policy];
    }

    [Theory]
    [MemberData(nameof(RoutesAndPolicies))]
    public async Task CollectorFailuresPropagateWithoutChangingIdentityOrPoisoningSearch(
        string mode, RegexTimeoutPolicy policy)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = 1,
            RegexEngine = mode.StartsWith("native", StringComparison.Ordinal) ? new RegexContractTestEngine() : null
        });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha"), ("b.txt", "alpha")]));
        using IIndexSnapshot snapshot = mode.Contains("overlay", StringComparison.Ordinal)
            ? index.OpenOverlaySnapshot()
            : index.OpenSnapshot(new SearchOptions(mode.Contains("parallel", StringComparison.Ordinal) ? 2 : 1));
        var ownOverlayRows = snapshot is IOverlaySnapshot && mode != "overlay-base";
        if (snapshot is IOverlaySnapshot overlay && ownOverlayRows)
            await overlay.Update(new InMemoryDocumentSource([("c.txt", "alpha")]), []);

        foreach (var (pattern, patternMode) in new[] { ("alpha", PatternMode.Literal),
            ("alpha", PatternMode.Regex), ("alp.a", PatternMode.Regex), ("(?=alpha)alpha", PatternMode.Regex) })
        foreach (var failure in new Exception[] { new RegexMatchTimeoutException("Collector timeout."),
            new InvalidDataException("Collector data failure."), new IOException("Collector I/O failure."),
            new InvalidOperationException("Collector operation failure.") })
        {
            var query = new SearchQuery(pattern, patternMode, CaseMode.Sensitive) { RegexTimeoutPolicy = policy };
            var collector = new ThrowingCollector(failure);

            var caught = Record.Exception(() => snapshot.Search(query, new(), collector));

            Assert.Same(failure, caught);
            Assert.Contains(nameof(ThrowingCollector.OnHit), failure.StackTrace ?? "");
            Assert.Equal(1, collector.Calls);
            Assert.Equal(ownOverlayRows ? 3 : 2, snapshot.Search(query, new(), new HitBuffer()).HitsReturned);
        }
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("parallel")]
    [InlineData("overlay")]
    public async Task ActualRegexPreparationAndMatchingTimeoutsKeepTheirPolicy(string mode)
    {
        foreach (var compilation in new[] { false, true })
        foreach (var policy in new[] { RegexTimeoutPolicy.StopSearch, RegexTimeoutPolicy.Throw })
        {
            var engine = new RegexContractTestEngine { TimeoutCompile = compilation, TimeoutMatch = !compilation };
            using var index = SiftIndex.CreateInMemory(new SiftOptions { TargetSegmentDocumentCount = 1, RegexEngine = engine });
            await index.Build(new InMemoryDocumentSource([("a.txt", "alpha"), ("b.txt", "alpha")]));
            using IIndexSnapshot snapshot = mode == "overlay" ? index.OpenOverlaySnapshot() :
                index.OpenSnapshot(new SearchOptions(mode == "parallel" ? 2 : 1));
            if (snapshot is IOverlaySnapshot overlay)
                await overlay.Update(new InMemoryDocumentSource([("c.txt", "alpha")]), []);
            var query = new SearchQuery("alp.a", PatternMode.Regex, CaseMode.Sensitive) { RegexTimeoutPolicy = policy };
            var hits = new HitBuffer();

            if (policy == RegexTimeoutPolicy.Throw)
                Assert.Throws<RegexMatchTimeoutException>(() => snapshot.Search(query, new(), hits));
            else
            {
                var stats = snapshot.Search(query, new(), hits);
                Assert.Equal(SearchStopReason.RegexTimeout, stats.StopReason);
                Assert.Equal(0, stats.HitsReturned);
            }
            Assert.Empty(hits.Hits);
        }
    }

    private sealed class ThrowingCollector(Exception failure) : IHitCollector
    {
        public int Calls { get; private set; }
        public bool OnHit(in RawHit hit) { Calls++; throw failure; }
    }
}
