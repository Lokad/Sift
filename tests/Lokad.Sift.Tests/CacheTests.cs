using Lokad.Sift.Search;

namespace Lokad.Sift.Tests;

[Collection("ProcessMetrics")]
public sealed class CacheTests
{
    [Fact]
    public async Task Utf8RegexSearchDoesNotPrepareUnusedUnicodeFallback()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var index = SiftIndex.CreateInMemory();
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
    public void BoundedLruCacheEvictsLeastRecentlyUsedEntry()
    {
        var cache = new BoundedLruCache<int, string>(capacity: 2);

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
        var cache = new BoundedLruCache<int, string>(capacity: 2);
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
    public void BoundedLruCacheRemovesFaultedCreation()
    {
        var cache = new BoundedLruCache<int, string>(capacity: 2);

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
    public void UserRegexCacheIsBounded()
    {
        for (var i = 0; i < RegexVerifier.CacheCapacity + 32; i++)
        {
            var query = new SearchQuery($"needle(?#bounded-cache-{i})", PatternMode.Regex, CaseMode.Sensitive);
            _ = RegexVerifier.GetRegex(query, CaseMode.Sensitive);
        }

        Assert.True(RegexVerifier.CacheCount <= RegexVerifier.CacheCapacity);
        Assert.True(RegexVerifier.CachePatternCharacters <= RegexVerifier.CachePatternCharacterCapacity);
    }

    [Fact]
    public async Task PreparedQueryAndPathFilterCachesAreBounded()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "needle")]));
        using var snapshot = index.OpenSnapshot();

        for (var i = 0; i < SearchSnapshot.PreparedQueryCacheCapacity + 32; i++)
        {
            var suffix = $"bounded-prepared-{i}";
            _ = snapshot.Explain(
                new SearchQuery($"needle(?#${suffix})", PatternMode.Regex, CaseMode.Sensitive),
                new PathFilter { PathRegex = $"src/.+(?#${suffix})" });
        }

        Assert.True(SearchSnapshot.PreparedQueryCacheCount <= SearchSnapshot.PreparedQueryCacheCapacity);
        Assert.True(SearchSnapshot.PreparedQueryCacheCharacters <= SearchSnapshot.PreparedQueryCacheCharacterCapacity);
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
