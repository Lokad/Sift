using System.Text.RegularExpressions;
using Lokad.Sift;

namespace Lokad.Sift.Tests;

public sealed class SearchControlRegressionTests
{
    public static IEnumerable<object[]> CancellationCases()
    {
        foreach (var mode in new[] { "snapshot", "overlay" })
        {
            yield return [mode, "alpha", PatternMode.Literal, CaseMode.Sensitive];
            yield return [mode, "alpha", PatternMode.Literal, CaseMode.Insensitive];
            yield return [mode, "alpha", PatternMode.Regex, CaseMode.Sensitive];
            yield return [mode, "alp.a", PatternMode.Regex, CaseMode.Sensitive];
            yield return [mode, "(?=alpha)alpha", PatternMode.Regex, CaseMode.Sensitive];
        }
    }

    [Theory]
    [MemberData(nameof(CancellationCases))]
    public async Task CancellationInFirstHitStopsBeforeTheNextHit(
        string mode, string pattern, PatternMode patternMode, CaseMode caseMode)
    {
        var content = string.Join(' ', Enumerable.Repeat("alpha", 50));
        using var index = SiftIndex.CreateInMemory(new SiftOptions { TargetSegmentDocumentCount = 1 });
        await index.Build(new InMemoryDocumentSource([("a.txt", content), ("b.txt", content)]));
        using IIndexSnapshot snapshot = mode == "overlay"
            ? index.OpenOverlaySnapshot()
            : index.OpenSnapshot();
        if (snapshot is IOverlaySnapshot overlay)
            await overlay.Update(new InMemoryDocumentSource([("c.txt", content)]), []);
        using var cancellation = new CancellationTokenSource();
        var collector = new ContinuingCallbackCollector(cancellation.Cancel);

        var failure = Assert.ThrowsAny<OperationCanceledException>(() => snapshot.Search(
            new SearchQuery(pattern, patternMode, caseMode), new PathFilter(), collector, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(1, collector.Count);
    }

    public static IEnumerable<object[]> DeadlineCases()
    {
        foreach (var mode in new[] { "snapshot", "overlay", "native" })
        {
            yield return [mode, "alpha", PatternMode.Literal, CaseMode.Sensitive];
            yield return [mode, "alpha", PatternMode.Literal, CaseMode.Insensitive];
            yield return [mode, "alpha", PatternMode.Regex, CaseMode.Sensitive];
            yield return [mode, "alp.a", PatternMode.Regex, CaseMode.Sensitive];
            yield return [mode, "(?=alpha)alpha", PatternMode.Regex, CaseMode.Sensitive];
            if (mode != "native")
                yield return [mode, "é", PatternMode.Literal, CaseMode.Sensitive];
        }
    }

    [Theory]
    [MemberData(nameof(DeadlineCases))]
    public async Task DeadlineStopsBetweenHitsWithinOneDocument(
        string mode, string pattern, PatternMode patternMode, CaseMode caseMode)
    {
        var word = pattern == "é" ? "é" : "alpha";
        var content = string.Join(' ', Enumerable.Repeat(word, 50));
        var engine = mode == "native" ? new RegexContractTestEngine
        {
            Ranges = Enumerable.Range(0, 50).Select(i => (i * 6, 5)).ToArray()
        } : null;
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = 1,
            RegexEngine = engine
        });
        await index.Build(new InMemoryDocumentSource([("a.txt", content), ("b.txt", content)]));
        using IIndexSnapshot snapshot = mode == "overlay"
            ? index.OpenOverlaySnapshot()
            : index.OpenSnapshot();
        if (snapshot is IOverlaySnapshot overlay)
            await overlay.Update(new InMemoryDocumentSource([("c.txt", content)]), []);
        var query = new SearchQuery(pattern, patternMode, caseMode);
        snapshot.Search(query, new PathFilter(), new HitBuffer());
        var paused = false;
        var collector = new ContinuingCallbackCollector(() =>
        {
            if (paused) return;
            paused = true;
            Thread.Sleep(400);
        });

        var stats = snapshot.Search(query with { MaxElapsedMilliseconds = 200 }, new PathFilter(), collector);

        Assert.Equal(1, collector.Count);
        Assert.Equal(1, stats.HitsReturned);
        Assert.Equal(SearchStopReason.MaxElapsedTime, stats.StopReason);
        if (mode == "native" && pattern == "alp.a")
            Assert.True(stats.Utf8RegexInvocations > 0);
    }

    [Theory]
    [InlineData("(?# [)")]
    [InlineData("(?# \\)")]
    [InlineData("(?x)# [\n")]
    [InlineData("(?X)# [\n")]
    [InlineData("(?x:# [\n)")]
    [InlineData("(?x:alpha)#")]
    [InlineData("(?x)alpha(?-x)#")]
    [InlineData("\\c[")]
    [InlineData("[-[()]")]
    public async Task CommentsAndEscapesCannotHideExcessiveNesting(string prefix)
    {
        var pattern = prefix + new string('(', 9) + "alpha" + new string(')', 9);
        _ = new Regex(pattern, RegexOptions.CultureInvariant);
        using var index = SiftIndex.CreateInMemory(new SiftOptions { MaxPatternNesting = 8 });
        await index.Build(new InMemoryDocumentSource([("alpha.txt", "alpha")]));
        using var snapshot = index.OpenSnapshot();

        Assert.Throws<ArgumentException>(() => snapshot.Search(
            new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), new HitBuffer()));
        Assert.Throws<ArgumentException>(() => snapshot.Explain(
            new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive), new PathFilter()));
        Assert.Throws<ArgumentException>(() => snapshot.Search(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathRegex = pattern }, new HitBuffer()));
        Assert.Throws<ArgumentException>(() => snapshot.Explain(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter { PathRegex = pattern }));
    }

    [Theory]
    [InlineData("(?# [((((((((()alpha")]
    [InlineData("(?x)# [(((((((((\nalpha")]
    [InlineData("(?x:# [(((((((((\nalpha)")]
    [InlineData("((?x)alpha)")]
    [InlineData("[](((((((((]*alpha")]
    [InlineData("[^]((((((((()]*alpha")]
    [InlineData("[a-z-[()]]*alpha")]
    [InlineData("[a-z-[b-[c]]]*alpha")]
    [InlineData("\\(\\(\\(\\(\\(\\(\\(\\(\\(alpha")]
    [InlineData("(?x)\\#\\(\\(\\(\\(\\(\\(\\(\\(\\(alpha")]
    [InlineData("[\\c]]*alpha")]
    public async Task LiteralAndCommentParenthesesDoNotConsumeTheNestingBudget(string pattern)
    {
        var oracle = new Regex(pattern, RegexOptions.CultureInvariant);
        using var index = SiftIndex.CreateInMemory(new SiftOptions { MaxPatternNesting = 1 });
        await index.Build(new InMemoryDocumentSource([("alpha.txt", "alpha")]));
        using var snapshot = index.OpenSnapshot();
        var contentHits = new HitBuffer();
        var pathHits = new HitBuffer();

        snapshot.Explain(new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive), new PathFilter());
        snapshot.Search(new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), contentHits);
        snapshot.Explain(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter { PathRegex = pattern });
        snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathRegex = pattern }, pathHits);

        Assert.Equal(oracle.Matches("alpha").Count, contentHits.Hits.Count);
        Assert.Equal(oracle.IsMatch("alpha.txt") ? 1 : 0, pathHits.Hits.Count);
    }

    [Theory]
    [InlineData("(?#unterminated")]
    [InlineData("(?x:alpha")]
    [InlineData("[abc")]
    [InlineData("\\c")]
    [InlineData("[a-z-[b]")]
    public async Task MalformedRegexSyntaxStillFailsValidation(string pattern)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Regex(pattern));
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("alpha.txt", "alpha")]));
        using var snapshot = index.OpenSnapshot();

        Assert.ThrowsAny<ArgumentException>(() => snapshot.Search(
            new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), new HitBuffer()));
        Assert.ThrowsAny<ArgumentException>(() => snapshot.Explain(
            new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive), new PathFilter()));
        Assert.ThrowsAny<ArgumentException>(() => snapshot.Search(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathRegex = pattern }, new HitBuffer()));
        Assert.ThrowsAny<ArgumentException>(() => snapshot.Explain(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter { PathRegex = pattern }));
    }

    private sealed class ContinuingCallbackCollector(Action callback) : IHitCollector
    {
        public int Count { get; private set; }
        public bool OnHit(in RawHit hit)
        {
            Count++;
            callback();
            return true;
        }
    }
}
