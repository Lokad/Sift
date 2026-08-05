using System.Numerics;
using Lokad.Sift.Indexer;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal sealed class LiveDocumentResolver(
    ISiftStorage storage,
    CorpusManifest manifest) : IDisposable
{
    private readonly Dictionary<SegmentId, MappedSegment> _openedSegments = [];

    public Dictionary<string, LiveDocumentKey> Resolve(
        IReadOnlySet<string> affectedPaths,
        CancellationToken cancellationToken)
    {
        return Resolve(
            affectedPaths,
            incomingDocuments: null,
            unchangedPaths: null,
            cancellationToken);
    }

    public Dictionary<string, LiveDocumentKey> ResolveAndFindUnchanged(
        IReadOnlySet<string> affectedPaths,
        IReadOnlyDictionary<string, StoredDocument> incomingDocuments,
        ISet<string> unchangedPaths,
        CancellationToken cancellationToken)
    {
        return Resolve(
            affectedPaths,
            incomingDocuments,
            unchangedPaths,
            cancellationToken);
    }

    public void Dispose()
    {
        foreach (var segment in _openedSegments.Values)
        {
            segment.Dispose();
        }

        _openedSegments.Clear();
    }

    private Dictionary<string, LiveDocumentKey> Resolve(
        IReadOnlySet<string> affectedPaths,
        IReadOnlyDictionary<string, StoredDocument>? incomingDocuments,
        ISet<string>? unchangedPaths,
        CancellationToken cancellationToken)
    {
        var live = new Dictionary<string, LiveDocumentKey>(affectedPaths.Count, StringComparer.Ordinal);
        if (affectedPaths.Count == 0)
        {
            return live;
        }

        var unresolvedPaths = affectedPaths.ToHashSet(StringComparer.Ordinal);
        string[]? sortedPaths = null;
        for (var entryIndex = manifest.Segments.Count - 1;
             entryIndex >= 0 && unresolvedPaths.Count > 0;
             entryIndex--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = manifest.Segments[entryIndex];
            var segment = OpenSegment(entry.SegmentId, cancellationToken);
            var binarySearchComparisons = (long)unresolvedPaths.Count *
                (BitOperations.Log2((uint)Math.Max(segment.DocumentCount, 1)) + 1);
            if (binarySearchComparisons <= segment.DocumentCount)
            {
                // A small batch can target paths near the end of a large segment,
                // for which a merge scan would traverse nearly every document.
                foreach (var path in affectedPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!unresolvedPaths.Contains(path) ||
                        !segment.TryFindDocumentOrdinal(path, out var foundOrdinal))
                    {
                        continue;
                    }

                    unresolvedPaths.Remove(path);
                    if (!entry.DeadOrdinals.Contains(foundOrdinal))
                    {
                        var document = segment.GetDocument(foundOrdinal);
                        var key = CreateKey(entry.SegmentId, foundOrdinal, document);
                        live.Add(path, key);
                        RecordUnchangedDocument(
                            segment,
                            document,
                            path,
                            key,
                            incomingDocuments,
                            unchangedPaths);
                    }
                }

                continue;
            }

            if (sortedPaths is null)
            {
                sortedPaths = affectedPaths.ToArray();
                Array.Sort(sortedPaths, StringComparer.Ordinal);
            }

            // Both inputs are ordinal-sorted, so one merge pass replaces a
            // binary search for every path in a large update batch.
            var pathIndex = 0;
            var ordinal = 0u;
            var deadIndex = 0;
            var comparisons = 0;
            while (pathIndex < sortedPaths.Length && ordinal < segment.DocumentCount)
            {
                while (pathIndex < sortedPaths.Length && !unresolvedPaths.Contains(sortedPaths[pathIndex]))
                {
                    pathIndex++;
                }

                if (pathIndex >= sortedPaths.Length)
                {
                    break;
                }

                if ((comparisons++ & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var document = segment.GetDocument(ordinal);
                var comparison = segment.ComparePathOrdinal(document, sortedPaths[pathIndex]);
                if (comparison < 0)
                {
                    ordinal++;
                    continue;
                }

                if (comparison > 0)
                {
                    pathIndex++;
                    continue;
                }

                while (deadIndex < entry.DeadOrdinals.Length && entry.DeadOrdinals[deadIndex] < ordinal)
                {
                    deadIndex++;
                }

                if (deadIndex >= entry.DeadOrdinals.Length || entry.DeadOrdinals[deadIndex] != ordinal)
                {
                    var path = sortedPaths[pathIndex];
                    var key = CreateKey(entry.SegmentId, ordinal, document);
                    live.Add(path, key);
                    RecordUnchangedDocument(
                        segment,
                        document,
                        path,
                        key,
                        incomingDocuments,
                        unchangedPaths);
                }

                unresolvedPaths.Remove(sortedPaths[pathIndex]);
                ordinal++;
                pathIndex++;
            }
        }

        return live;
    }

    private MappedSegment OpenSegment(
        SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        if (_openedSegments.TryGetValue(segmentId, out var segment))
        {
            return segment;
        }

        segment = storage.OpenSegment(segmentId, cancellationToken);
        _openedSegments.Add(segmentId, segment);
        return segment;
    }

    private static LiveDocumentKey CreateKey(
        SegmentId segmentId,
        uint ordinal,
        SegmentDocument document)
    {
        return new LiveDocumentKey(
            segmentId,
            ordinal,
            document.ContentLength,
            document.MTimeUtcTicks,
            document.ContentHash64);
    }

    private static void RecordUnchangedDocument(
        MappedSegment segment,
        SegmentDocument indexedDocument,
        string path,
        in LiveDocumentKey indexedKey,
        IReadOnlyDictionary<string, StoredDocument>? incomingDocuments,
        ISet<string>? unchangedPaths)
    {
        if (incomingDocuments is null ||
            unchangedPaths is null ||
            !incomingDocuments.TryGetValue(path, out var incoming) ||
            indexedKey.ContentLength != incoming.Content.Length ||
            indexedKey.ContentHash64 != incoming.ContentHash64 ||
            indexedKey.MTimeUtcTicks != incoming.MTimeUtcTicks)
        {
            return;
        }

        // The persisted hash is only a fast rejection filter; byte equality
        // is required before suppressing a replacement.
        if (segment.GetContentBytes(indexedDocument).SequenceEqual(incoming.Content.Span))
        {
            unchangedPaths.Add(path);
        }
    }
}

internal readonly record struct LiveDocumentKey(
    SegmentId SegmentId,
    uint Ordinal,
    int ContentLength,
    long MTimeUtcTicks,
    ulong ContentHash64);
