using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal ref struct SegmentScopeBuilder(
    SegmentCache cache,
    PathFilterMatcher matcher,
    int maxSelectedNgrams,
    CancellationToken cancellationToken)
{
    public SegmentPathScope Build()
    {
        var prefixRange = matcher.PathPrefix is null
            ? (StartInclusive: 0u, EndExclusive: (uint)cache.Segment.DocumentCount)
            : cache.Segment.FindPathPrefixRange(matcher.PathPrefix, matcher.PathPrefixUpperBound);
        var prefixDocuments = (int)(prefixRange.EndExclusive - prefixRange.StartInclusive);

        if (!matcher.RequiresPathVerification && !matcher.UsesPathIndex)
        {
            var ordinals = OrdinalSet.FromRange(
                prefixRange.StartInclusive,
                prefixRange.EndExclusive,
                cache.DeadOrdinals);
            if (prefixRange.StartInclusive == 0 &&
                prefixRange.EndExclusive == (uint)cache.Segment.DocumentCount)
            {
                return CreateResult(
                    new SegmentScope(
                        ordinals,
                        cache.LiveContentBytes,
                        cache.LiveNonAsciiDocuments,
                        cache.LiveNonAsciiBytes),
                    prefixDocuments,
                    prefixDocuments);
            }

            var metrics = GetScopeMetrics(ordinals);
            return CreateResult(
                new SegmentScope(
                    ordinals,
                    metrics.ContentBytes,
                    metrics.NonAsciiDocuments,
                    metrics.NonAsciiBytes),
                prefixDocuments,
                prefixDocuments);
        }

        OrdinalSet scopedOrdinals;
        if (matcher.UsesPathIndex)
        {
            var indexPair = new IndexRegionPair(
                SegmentRegionId.PathTriCsLex,
                SegmentRegionId.PathTriCsPost);
            var selectedPathTrigrams = SelectPathTrigrams(
                indexPair,
                matcher.GetPathTrigrams());
            scopedOrdinals = CandidateGenerator.Generate(
                cache.Segment,
                OrdinalSet.FromRange(
                    prefixRange.StartInclusive,
                    prefixRange.EndExclusive,
                    DeadOrdinalSet.Empty),
                indexPair,
                selectedPathTrigrams).Ordinals;
        }
        else
        {
            scopedOrdinals = OrdinalSet.FromRange(
                prefixRange.StartInclusive,
                prefixRange.EndExclusive,
                DeadOrdinalSet.Empty);
        }

        var pathIndexCandidates = scopedOrdinals.Count;

        var acceptedOrdinals = GC.AllocateUninitializedArray<uint>(scopedOrdinals.Count);
        var acceptedCount = 0;
        long bytes = 0;
        var nonAsciiDocuments = 0;
        long nonAsciiBytes = 0;
        var deadIndex = 0;
        var scanned = 0;
        foreach (var ordinal in scopedOrdinals)
        {
            if ((scanned++ & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            while (deadIndex < cache.DeadOrdinals.Length &&
                   cache.DeadOrdinals[deadIndex] < ordinal)
            {
                deadIndex++;
            }

            if (deadIndex < cache.DeadOrdinals.Length &&
                cache.DeadOrdinals[deadIndex] == ordinal)
            {
                continue;
            }

            var document = cache.Segment.GetDocument(ordinal);
            if (matcher.RequiresPathVerification)
            {
                var isMatch = matcher.RequiresStringPathVerification
                    ? matcher.IsMatch(cache.Segment.GetPath(document))
                    : matcher.IsMatch(cache.Segment.GetPathBytes(document));
                if (!isMatch)
                {
                    continue;
                }
            }

            acceptedOrdinals[acceptedCount++] = ordinal;
            bytes += document.ContentLength;
            if ((document.Flags & SegmentDocumentFlags.Ascii) == 0)
            {
                nonAsciiDocuments++;
                nonAsciiBytes += document.ContentLength;
            }
        }

        return CreateResult(
            new SegmentScope(
                // OrdinalSet owns the populated prefix, avoiding a second scope-sized array.
                OrdinalSet.FromSorted(acceptedOrdinals, acceptedCount),
                bytes,
                nonAsciiDocuments,
                nonAsciiBytes),
            prefixDocuments,
            pathIndexCandidates);
    }

    private SegmentPathScope CreateResult(
        SegmentScope scope,
        int prefixDocuments,
        int pathIndexCandidates)
    {
        double estimatedCost = prefixDocuments;
        if (matcher.UsesPathIndex)
        {
            estimatedCost += pathIndexCandidates * 0.5d;
        }

        if (matcher.RequiresPathVerification)
        {
            estimatedCost += matcher.RequiresStringPathVerification ? scope.Count * 4d : scope.Count;
        }

        return new SegmentPathScope(scope, estimatedCost);
    }

    private int[] SelectPathTrigrams(
        IndexRegionPair indexPair,
        ReadOnlySpan<int> pathTrigrams)
    {
        var selected = new List<(int Gram, int DocumentFrequency)>(
            Math.Min(pathTrigrams.Length, maxSelectedNgrams));
        for (var i = 0; i < pathTrigrams.Length; i++)
        {
            var gram = pathTrigrams[i];
            if (!cache.Segment.TryGetPostings(indexPair, gram, out _, out var documentFrequency))
            {
                // One absent mandatory gram proves that the path scope is empty.
                return [gram];
            }

            var insertionIndex = 0;
            while (insertionIndex < selected.Count &&
                   selected[insertionIndex].DocumentFrequency <= documentFrequency)
            {
                insertionIndex++;
            }

            if (insertionIndex >= maxSelectedNgrams)
            {
                continue;
            }

            selected.Insert(insertionIndex, (gram, documentFrequency));
            if (selected.Count > maxSelectedNgrams)
            {
                selected.RemoveAt(maxSelectedNgrams);
            }
        }

        var result = new int[selected.Count];
        for (var i = 0; i < selected.Count; i++)
        {
            result[i] = selected[i].Gram;
        }

        return result;
    }

    private ScopeMetrics GetScopeMetrics(OrdinalSet ordinals)
    {
        long bytes = 0;
        var nonAsciiDocuments = 0;
        long nonAsciiBytes = 0;
        var scanned = 0;
        foreach (var ordinal in ordinals)
        {
            if ((scanned++ & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var document = cache.Segment.GetDocument(ordinal);
            bytes += document.ContentLength;
            if ((document.Flags & SegmentDocumentFlags.Ascii) == 0)
            {
                nonAsciiDocuments++;
                nonAsciiBytes += document.ContentLength;
            }
        }

        return new ScopeMetrics(bytes, nonAsciiDocuments, nonAsciiBytes);
    }
}

internal readonly record struct SegmentPathScope(
    SegmentScope Scope,
    double EstimatedCost);

internal readonly record struct ScopeMetrics(
    long ContentBytes,
    int NonAsciiDocuments,
    long NonAsciiBytes);
