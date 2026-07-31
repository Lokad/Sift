namespace Lokad.Sift.Search;

public static class SearchProcessMetrics
{
    public static long RegexCacheHits => RegexVerifier.CacheHits;
    public static long RegexCacheMisses => RegexVerifier.CacheMisses;
    public static int RegexCacheEntries => RegexVerifier.CacheCount;
    public static int RegexCachePatternCharacters => RegexVerifier.CachePatternCharacters;
    public static int Utf8RegexCacheEntries => Utf8RegexEngine.CacheCount;
    public static int Utf8RegexCachePatternCharacters => Utf8RegexEngine.CachePatternCharacters;

    public static double RegexCacheHitRate
    {
        get
        {
            var hits = RegexCacheHits;
            var misses = RegexCacheMisses;
            var total = hits + misses;
            return total == 0 ? 0d : (double)hits / total;
        }
    }
}
