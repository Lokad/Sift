using Lokad.Sift.Search;

namespace Lokad.Sift.Tests;

public sealed class CacheTests
{
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
    public void UserRegexCacheIsBounded()
    {
        for (var i = 0; i < RegexVerifier.CacheCapacity + 32; i++)
        {
            var query = new SearchQuery($"needle(?#bounded-cache-{i})", PatternMode.Regex, CaseMode.Sensitive);
            _ = RegexVerifier.GetRegex(query, CaseMode.Sensitive);
        }

        Assert.True(RegexVerifier.CacheCount <= RegexVerifier.CacheCapacity);
    }
}
