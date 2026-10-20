using Lokad.Sift.Search;
using Lokad.Sift.Storage;

namespace Lokad.Sift;

/// <summary>Process-wide resource and cache counters maintained by Sift.</summary>
public static class SiftProcessMetrics
{
    public static long MappedSegmentBytes => MappedSegment.CurrentMappedBytes;
    public static int OpenSegments => MappedSegment.CurrentOpenSegments;
    public static long NativeIndexWorkspaceBytes => DirectIndexedGramBuilder.CurrentNativeWorkspaceBytes;
    /// <summary>Gets hits at the process-wide managed regex compilation cache.</summary>
    /// <remarks>Reusing an index's prepared query does not access this cache.</remarks>
    public static long RegexCacheHits => RegexVerifier.CacheHits;
    /// <summary>Gets misses at the process-wide managed regex compilation cache.</summary>
    public static long RegexCacheMisses => RegexVerifier.CacheMisses;
    public static int RegexCacheEntries => RegexVerifier.CacheCount;
    public static int RegexCachePatternCharacters => RegexVerifier.CachePatternCharacters;
    public static int Utf8RegexCacheEntries => RegexExecutionContext.NativeEntries;
    public static int Utf8RegexCachePatternCharacters => RegexExecutionContext.NativePatternCharacters;

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
