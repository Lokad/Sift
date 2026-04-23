namespace Lokad.Sift.Storage;

public sealed class InMemorySiftStorage : ISiftStorage
{
    private readonly object _writerLock = new();
    private readonly Dictionary<ulong, byte[]> _segments = new();
    private CorpusManifest? _manifest;

    public bool HasManifest => _manifest is not null;

    public IDisposable AcquireWriterLock(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (System.Threading.Monitor.TryEnter(_writerLock, millisecondsTimeout: 50))
            {
                return new Releaser(_writerLock);
            }
        }
    }

    public BuildPublication PublishFullBuild(IReadOnlyList<IReadOnlyList<StoredDocument>> segments, CancellationToken cancellationToken)
    {
        var existing = _manifest;
        var corpusId = existing?.CorpusId ?? Guid.NewGuid();
        var generation = (existing?.Generation ?? 0UL) + 1UL;
        var entries = new List<SegmentManifestEntry>(segments.Count);
        var totalDocuments = 0;
        long totalBytes = 0;
        ulong segmentSeed = generation ^ ((ulong)DateTime.UtcNow.Ticks << 1);
        for (var i = 0; i < segments.Count; i++)
        {
            var segmentId = segmentSeed + (ulong)i;
            var entry = PublishSegment(segments[i], segmentId, cancellationToken);
            entries.Add(entry);
            totalDocuments += entry.DocumentCount;
            totalBytes += entry.ContentBytes;
        }

        _manifest = CloneManifest(new CorpusManifest(corpusId, generation, entries));
        return new BuildPublication(totalDocuments, entries.Count, totalBytes);
    }

    public SegmentManifestEntry PublishSegment(IReadOnlyList<ISegmentBuildDocument> documents, ulong segmentId, CancellationToken cancellationToken)
    {
        var (bytes, contentBytes) = SegmentWriter.WriteToBytes(segmentId, documents, cancellationToken);
        using var _ = new MappedSegment(bytes, validateChecksums: true);
        _segments[segmentId] = bytes;
        return new SegmentManifestEntry(segmentId, documents.Count, contentBytes, []);
    }

    public void PublishManifest(CorpusManifest manifest)
    {
        _manifest = CloneManifest(manifest);
    }

    public CorpusManifest ReadManifest()
    {
        return _manifest is not null
            ? CloneManifest(_manifest)
            : throw new FileNotFoundException("Sift manifest is not present in memory-backed storage.");
    }

    public MappedSegment OpenSegment(ulong segmentId, bool validateChecksums = true)
    {
        if (!_segments.TryGetValue(segmentId, out var bytes))
        {
            throw new FileNotFoundException($"Missing in-memory Sift segment '{segmentId:X16}'.");
        }

        return new MappedSegment(bytes, validateChecksums);
    }

    private static CorpusManifest CloneManifest(CorpusManifest manifest)
    {
        var segments = new SegmentManifestEntry[manifest.Segments.Count];
        for (var i = 0; i < manifest.Segments.Count; i++)
        {
            var segment = manifest.Segments[i];
            segments[i] = segment with { DeadOrdinals = [.. segment.DeadOrdinals] };
        }

        return new CorpusManifest(manifest.CorpusId, manifest.Generation, segments);
    }

    private sealed class Releaser(object syncRoot) : IDisposable
    {
        private object? _syncRoot = syncRoot;

        public void Dispose()
        {
            var syncRoot = Interlocked.Exchange(ref _syncRoot, null);
            if (syncRoot is not null)
            {
                System.Threading.Monitor.Exit(syncRoot);
            }
        }
    }
}
