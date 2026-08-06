namespace Lokad.Sift.Storage;

internal sealed class CorpusManifestCache
{
    private readonly object _sync = new();
    private CacheEntry? _cached;
#if SIFT_INTERNAL_VERIFICATION
    private long _parseCount;

    public long ParseCount => Interlocked.Read(ref _parseCount);
    internal Action? BeforeRefreshCacheStore { get; set; }
#endif

    public CorpusManifest Read(string manifestPath)
    {
        var stamp = ReadStamp(manifestPath);
        lock (_sync)
        {
            if (_cached is { } cached && cached.Stamp == stamp)
            {
                return cached.Manifest;
            }
        }

        return Refresh(manifestPath, stamp);
    }

    public CorpusManifest Refresh(string manifestPath)
    {
        return Refresh(manifestPath, ReadStamp(manifestPath));
    }

    private CorpusManifest Refresh(string manifestPath, ManifestStamp stamp)
    {
        var manifest = CorpusManifestCodec.Read(manifestPath);
#if SIFT_INTERNAL_VERIFICATION
        Interlocked.Increment(ref _parseCount);
#endif
        var currentStamp = ReadStamp(manifestPath);
        if (currentStamp == stamp && manifest.Generation.Value == stamp.Generation)
        {
#if SIFT_INTERNAL_VERIFICATION
            BeforeRefreshCacheStore?.Invoke();
#endif
            lock (_sync)
            {
                // Parsing deliberately happens outside the lock. A publisher may have
                // installed a newer generation meanwhile; never regress that cache entry.
                if (_cached is not { } cached || cached.Stamp.Generation <= stamp.Generation)
                {
                    _cached = new CacheEntry(stamp, manifest);
                }
            }
        }

        return manifest;
    }

    public void Store(string manifestPath, CorpusManifest manifest)
    {
        lock (_sync)
        {
            _cached = new CacheEntry(ReadStamp(manifestPath), manifest);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _cached = null;
        }
    }

    private static ManifestStamp ReadStamp(string manifestPath)
    {
        using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < SegmentFormat.LegacyManifestHeaderSize + sizeof(ulong))
        {
            throw new InvalidDataException("Invalid Sift manifest length.");
        }

        Span<byte> header = stackalloc byte[36];
        stream.ReadExactly(header);
        if (LittleEndianCodec.ReadUInt64(header) != SegmentFormat.ManifestMagic)
        {
            throw new InvalidDataException("Invalid Sift manifest magic.");
        }

        return new ManifestStamp(
            LittleEndianCodec.ReadUInt64(header[28..]),
            stream.Length,
            File.GetLastWriteTimeUtc(manifestPath).Ticks);
    }

    private readonly record struct ManifestStamp(
        ulong Generation,
        long Length,
        long LastWriteUtcTicks);

    private sealed record CacheEntry(
        ManifestStamp Stamp,
        CorpusManifest Manifest);
}
