using Lokad.Sift.Search;

namespace Lokad.Sift.Tests;

[Collection("ProcessMetrics")]
public sealed class CacheTests
{
    [Theory]
    [InlineData(PatternMode.Literal)]
    [InlineData(PatternMode.Regex)]
    public async Task LiteralQueriesUseLiteralVerification(PatternMode mode)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("alpha", mode, CaseMode.Sensitive);

        Assert.True(snapshot.Explain(query, new PathFilter()).CanUseLiteralVerifier);
        var stats = snapshot.Search(query, new PathFilter(), new HitBuffer());
        Assert.Equal(1, stats.HitsReturned);
        Assert.Equal(0, stats.RegexInvocations);
    }


    [Fact]
    public async Task Utf8RegexSearchDoesNotPrepareUnusedUnicodeFallback()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = new RegexContractTestEngine() });
        await index.Build(new InMemoryDocumentSource([("src/a.txt", $"alpha123{suffix}")]));
        using var snapshot = index.OpenSnapshot();
        var baselineHits = RegexVerifier.CacheHits;
        var baselineMisses = RegexVerifier.CacheMisses;

        var stats = snapshot.Search(
            new SearchQuery($"alpha[0-9]*{suffix}", PatternMode.Regex, CaseMode.Sensitive),
            new PathFilter(),
            new HitBuffer());

        Assert.Equal(1, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
        Assert.Equal(baselineHits, RegexVerifier.CacheHits);
        Assert.Equal(baselineMisses, RegexVerifier.CacheMisses);
    }

    [Fact]
    public async Task ConcurrentPreparedQueryUsersReportOneUtf8RegexMiss()
    {
        const int concurrency = 8;
        var suffix = Guid.NewGuid().ToString("N");
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = new RegexContractTestEngine() });
        await index.Build(new InMemoryDocumentSource([("src/a.txt", $"alpha123{suffix}")]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery($"alpha[0-9]*{suffix}", PatternMode.Regex, CaseMode.Sensitive);
        using var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return snapshot.Search(query, new PathFilter(), new HitBuffer());
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Sum(static result => result.Utf8RegexCacheMisses));
        Assert.Equal(concurrency - 1, results.Sum(static result => result.Utf8RegexCacheHits));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task NativeCacheAccessIsCountedOncePerSearch(bool overlay, int parallelism)
    {
        var engine = new RegexContractTestEngine();
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            RegexEngine = engine,
            TargetSegmentDocumentCount = 1
        });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1"), ("b.txt", "alpha2")]));
        using IIndexSnapshot snapshot = overlay
            ? index.OpenOverlaySnapshot(new SearchOptions(parallelism))
            : index.OpenSnapshot(new SearchOptions(parallelism));
        if (snapshot is IOverlaySnapshot editable)
            await editable.Update(new InMemoryDocumentSource([("c.txt", "alpha3"), ("d.txt", "alpha4")]), []);
        var query = new SearchQuery("alpha[0-9]", PatternMode.Regex, CaseMode.Sensitive);

        var cold = snapshot.Search(query, new(), new HitBuffer());
        var warm = snapshot.Search(query, new(), new HitBuffer());

        Assert.Equal(overlay ? 4 : 2, cold.HitsReturned);
        Assert.Equal(cold.HitsReturned, warm.HitsReturned);
        Assert.Equal(1, cold.Utf8RegexCacheMisses);
        Assert.Equal(0, cold.Utf8RegexCacheHits);
        Assert.Equal(0, warm.Utf8RegexCacheMisses);
        Assert.Equal(1, warm.Utf8RegexCacheHits);
        Assert.Equal(1, engine.Compilations);
    }

    [Fact]
    public async Task ManagedCountersCountCompilationCacheLookups()
    {
        var query = new SearchQuery($"alpha[0-9]+(?#metrics-{Guid.NewGuid():N})", PatternMode.Regex, CaseMode.Sensitive);
        using var firstIndex = SiftIndex.CreateInMemory();
        await firstIndex.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var firstSnapshot = firstIndex.OpenSnapshot();
        var baselineHits = RegexVerifier.CacheHits;
        var baselineMisses = RegexVerifier.CacheMisses;

        Assert.Equal(1, firstSnapshot.Search(query, new(), new HitBuffer()).HitsReturned);
        Assert.Equal(baselineMisses + 1, RegexVerifier.CacheMisses);
        Assert.Equal(baselineHits, RegexVerifier.CacheHits);
        Assert.Equal(1, firstSnapshot.Search(query, new(), new HitBuffer()).HitsReturned);
        Assert.Equal(baselineMisses + 1, RegexVerifier.CacheMisses);
        Assert.Equal(baselineHits, RegexVerifier.CacheHits);

        using var secondIndex = SiftIndex.CreateInMemory();
        await secondIndex.Build(new InMemoryDocumentSource([("a.txt", "alpha2")]));
        using var secondSnapshot = secondIndex.OpenSnapshot();
        Assert.Equal(1, secondSnapshot.Search(query, new(), new HitBuffer()).HitsReturned);
        Assert.Equal(baselineMisses + 1, RegexVerifier.CacheMisses);
        Assert.Equal(baselineHits + 1, RegexVerifier.CacheHits);
    }

    [Fact]
    public void BoundedLruCacheEvictsLeastRecentlyUsedEntry()
    {
        var cache = new BoundedLruCache<int, string>(capacity: 2, maximumWeight: 2, static _ => 1);

        Assert.Equal("one", cache.GetOrAdd(1, static _ => "one", out var firstHit));
        Assert.False(firstHit);
        Assert.Equal("two", cache.GetOrAdd(2, static _ => "two", out _));
        Assert.Equal("one", cache.GetOrAdd(1, static _ => "unused", out var repeatedHit));
        Assert.True(repeatedHit);
        Assert.Equal("three", cache.GetOrAdd(3, static _ => "three", out _));

        Assert.Equal(2, cache.Count);
        Assert.Equal("two-again", cache.GetOrAdd(2, static _ => "two-again", out var evictedHit));
        Assert.False(evictedHit);
    }

    [Fact]
    public async Task BoundedLruCacheCoalescesConcurrentCreation()
    {
        const int concurrency = 8;
        var cache = new BoundedLruCache<int, string>(capacity: 2, maximumWeight: 2, static _ => 1);
        using var barrier = new Barrier(concurrency);
        var creations = 0;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(worker => Task.Run(() =>
            {
                _ = worker;
                barrier.SignalAndWait();
                return cache.GetOrAdd(
                    1,
                    cacheKey =>
                    {
                        _ = cacheKey;
                        Interlocked.Increment(ref creations);
                        return "one";
                    },
                    out _);
            }))
            .ToArray();

        var values = await Task.WhenAll(tasks);

        Assert.All(values, static value => Assert.Equal("one", value));
        Assert.Equal(1, creations);
    }

    [Fact]
    public async Task BoundedLruCacheDoesNotEvictAnInFlightFactory()
    {
        var cache = new BoundedLruCache<int, string>(capacity: 1, maximumWeight: 1, static _ => 1);
        var factoryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var creations = 0;
        var first = Task.Run(() => cache.GetOrAdd(
            1,
            _ =>
            {
                Interlocked.Increment(ref creations);
                factoryStarted.SetResult();
                releaseFactory.Task.GetAwaiter().GetResult();
                return "one";
            },
            out _));
        await factoryStarted.Task;
        Assert.Equal("two", cache.GetOrAdd(2, static _ => "two", out _));
        var second = Task.Run(() => cache.GetOrAdd(
            1,
            _ =>
            {
                Interlocked.Increment(ref creations);
                return "duplicate";
            },
            out _));

        releaseFactory.SetResult();
        var values = await Task.WhenAll(first, second);

        Assert.Equal(["one", "one"], values);
        Assert.Equal(1, creations);
        Assert.True(cache.Count <= 1);
    }

    [Fact]
    public void BoundedLruCacheRemovesFaultedCreation()
    {
        var cache = new BoundedLruCache<int, string>(capacity: 2, maximumWeight: 2, static _ => 1);

        Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrAdd(1, static _ => throw new InvalidOperationException("failure"), out _));

        Assert.Equal("recovered", cache.GetOrAdd(1, static _ => "recovered", out var cacheHit));
        Assert.False(cacheHit);
    }

    [Fact]
    public void BoundedLruCacheEvictsByAggregateWeight()
    {
        var cache = new BoundedLruCache<int, string>(
            capacity: 10,
            maximumWeight: 5,
            static key => key);

        _ = cache.GetOrAdd(2, static _ => "two", out _);
        _ = cache.GetOrAdd(3, static _ => "three", out _);
        _ = cache.GetOrAdd(4, static _ => "four", out _);

        Assert.Equal(1, cache.Count);
        Assert.Equal(4, cache.Weight);
    }

    [Fact]
    public async Task UserRegexCacheIsBounded()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "needle")]));
        using var snapshot = index.OpenSnapshot();
        for (var i = 0; i < RegexVerifier.CacheCapacity + 32; i++)
        {
            var query = new SearchQuery($"needle(?#bounded-cache-{i})", PatternMode.Regex, CaseMode.Sensitive);
            var stats = snapshot.Search(query, new PathFilter(), new HitBuffer());
            Assert.Equal(1, stats.HitsReturned);
            Assert.Equal(1, stats.FallbackRegexInvocations);
        }

        Assert.True(SiftProcessMetrics.RegexCacheEntries <= RegexVerifier.CacheCapacity);
        Assert.True(SiftProcessMetrics.RegexCachePatternCharacters <= RegexVerifier.CachePatternCharacterCapacity);
    }

    [Fact]
    public async Task PreparedQueryAndPathFilterCachesAreBounded()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "needle")]));
        using var snapshot = index.OpenSnapshot();

        for (var i = 0; i < SearchQueryPreparer.CacheCapacity + 32; i++)
        {
            var suffix = $"bounded-prepared-{i}";
            _ = snapshot.Explain(
                new SearchQuery($"needle(?#${suffix})", PatternMode.Regex, CaseMode.Sensitive),
                new PathFilter { PathRegex = $"src/.+(?#${suffix})" });
        }

        Assert.True(SearchQueryPreparer.CacheCount <= SearchQueryPreparer.CacheCapacity);
        Assert.True(SearchQueryPreparer.CacheCharacters <= SearchQueryPreparer.CacheCharacterCapacity);
        Assert.True(PathFilterMatcher.CacheCount <= PathFilterMatcher.CacheCapacity);
        Assert.True(PathFilterMatcher.CacheCharacters <= PathFilterMatcher.CacheCharacterCapacity);
    }

    [Fact]
    public void PreparedPathFilterDoesNotObserveCallerArrayMutation()
    {
        var includes = new[] { "src/**" };
        var matcher = PathFilterMatcher.Prepare(new PathFilter { IncludeGlobs = includes });

        includes[0] = "tests/**";

        Assert.True(matcher.IsMatch("src/a.txt"));
        Assert.False(matcher.IsMatch("tests/a.txt"));
        Assert.Same(
            matcher,
            PathFilterMatcher.Prepare(new PathFilter { IncludeGlobs = ["src/**"] }));
    }
}
