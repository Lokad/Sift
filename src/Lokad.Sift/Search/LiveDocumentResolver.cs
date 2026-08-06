using System.Buffers;
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
            var segment = OpenSegment(entry, cancellationToken);
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
                }

                unresolvedPaths.Remove(sortedPaths[pathIndex]);
                ordinal++;
                pathIndex++;
            }
        }

        return live;
    }

    public void ResolveExistingAndRemoveUnchanged(
        List<StoredDocument> documents,
        IDictionary<string, LiveDocumentKey> existingKeys,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return;
        }

        var states = ArrayPool<byte>.Shared.Rent(documents.Count);
        Array.Clear(states, 0, documents.Count);
        try
        {
            var unresolvedCount = documents.Count;
            for (var entryIndex = manifest.Segments.Count - 1;
                 entryIndex >= 0 && unresolvedCount > 0;
                 entryIndex--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = manifest.Segments[entryIndex];
                var segment = OpenSegment(entry, cancellationToken);
                var binarySearchComparisons = (long)unresolvedCount *
                    (BitOperations.Log2((uint)Math.Max(segment.DocumentCount, 1)) + 1);
                if (binarySearchComparisons <= segment.DocumentCount)
                {
                    for (var incomingIndex = 0; incomingIndex < documents.Count; incomingIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (states[incomingIndex] != 0 ||
                            !segment.TryFindDocumentOrdinal(documents[incomingIndex].Path, out var foundOrdinal))
                        {
                            continue;
                        }

                        states[incomingIndex] = 1;
                        unresolvedCount--;
                        if (!entry.DeadOrdinals.Contains(foundOrdinal))
                        {
                            var indexedDocument = segment.GetDocument(foundOrdinal);
                            RecordExistingDocument(
                                segment,
                                indexedDocument,
                                entry.SegmentId,
                                foundOrdinal,
                                documents[incomingIndex],
                                existingKeys,
                                ref states[incomingIndex]);
                        }
                    }

                    continue;
                }

                // Incoming partitions and segment documents are both path-sorted.
                // Preserve that ordering instead of rebuilding and sorting path sets.
                var documentIndex = 0;
                var ordinal = 0u;
                var deadIndex = 0;
                var comparisons = 0;
                while (documentIndex < documents.Count && ordinal < segment.DocumentCount)
                {
                    while (documentIndex < documents.Count && states[documentIndex] != 0)
                    {
                        documentIndex++;
                    }

                    if (documentIndex >= documents.Count)
                    {
                        break;
                    }

                    if ((comparisons++ & 0xFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var indexedDocument = segment.GetDocument(ordinal);
                    var comparison = segment.ComparePathOrdinal(
                        indexedDocument,
                        documents[documentIndex].Path);
                    if (comparison < 0)
                    {
                        ordinal++;
                        continue;
                    }

                    if (comparison > 0)
                    {
                        documentIndex++;
                        continue;
                    }

                    while (deadIndex < entry.DeadOrdinals.Length &&
                           entry.DeadOrdinals[deadIndex] < ordinal)
                    {
                        deadIndex++;
                    }

                    states[documentIndex] = 1;
                    unresolvedCount--;
                    if (deadIndex >= entry.DeadOrdinals.Length ||
                        entry.DeadOrdinals[deadIndex] != ordinal)
                    {
                        RecordExistingDocument(
                            segment,
                            indexedDocument,
                            entry.SegmentId,
                            ordinal,
                            documents[documentIndex],
                            existingKeys,
                            ref states[documentIndex]);
                    }

                    ordinal++;
                    documentIndex++;
                }
            }

            var retainedCount = 0;
            for (var documentIndex = 0; documentIndex < documents.Count; documentIndex++)
            {
                if (states[documentIndex] != 2)
                {
                    documents[retainedCount++] = documents[documentIndex];
                }
            }

            documents.RemoveRange(retainedCount, documents.Count - retainedCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(states);
        }
    }

    public void Dispose()
    {
        foreach (var segment in _openedSegments.Values)
        {
            segment.Dispose();
        }

        _openedSegments.Clear();
    }

    private MappedSegment OpenSegment(
        SegmentManifestEntry entry,
        CancellationToken cancellationToken)
    {
        var segmentId = entry.SegmentId;
        if (_openedSegments.TryGetValue(segmentId, out var segment))
        {
            return segment;
        }

        segment = storage.OpenSegment(entry, cancellationToken);
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

    private static void RecordExistingDocument(
        MappedSegment segment,
        SegmentDocument indexedDocument,
        SegmentId segmentId,
        uint ordinal,
        StoredDocument incomingDocument,
        IDictionary<string, LiveDocumentKey> existingKeys,
        ref byte state)
    {
        var indexedKey = CreateKey(segmentId, ordinal, indexedDocument);
        existingKeys.Add(incomingDocument.Path, indexedKey);
        if (indexedKey.ContentLength != incomingDocument.Content.Length ||
            indexedKey.ContentHash64 != incomingDocument.ContentHash64 ||
            indexedKey.MTimeUtcTicks != incomingDocument.MTimeUtcTicks)
        {
            return;
        }

        // The persisted hash is only a fast rejection filter; byte equality
        // is required before suppressing a replacement.
        if (segment.GetContentBytes(indexedDocument).SequenceEqual(incomingDocument.Content.Span))
        {
            state = 2;
        }
    }
}

internal readonly record struct LiveDocumentKey(
    SegmentId SegmentId,
    uint Ordinal,
    int ContentLength,
    long MTimeUtcTicks,
    ulong ContentHash64);
