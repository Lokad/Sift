using System.Buffers;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal static class CandidateGenerator
{
    public static CandidateResult Generate(MappedSegment segment, OrdinalSet scopeOrdinals, in LiteralPlan plan)
    {
        if (!plan.UsesIndex)
        {
            return new CandidateResult(scopeOrdinals, 0);
        }

        if (plan.IsMissingGram)
        {
            return new CandidateResult(OrdinalSet.Empty, 0);
        }

        var rentedGrams = plan.SelectedGrams.Length > 64
            ? ArrayPool<int>.Shared.Rent(plan.SelectedGrams.Length)
            : null;
        Span<int> grams = rentedGrams is null
            ? stackalloc int[plan.SelectedGrams.Length]
            : rentedGrams.AsSpan(0, plan.SelectedGrams.Length);
        for (var i = 0; i < grams.Length; i++)
        {
            grams[i] = plan.SelectedGrams[i].Gram;
        }

        try
        {
            return plan.UnionCandidates
                ? plan.UnionGroupLengths is not null && plan.UnionGroupLengths.Length > 0
                    ? GenerateUnionOfIntersections(segment, scopeOrdinals, plan.IndexPair, grams, plan.UnionGroupLengths)
                    : GenerateUnion(segment, scopeOrdinals, plan.IndexPair, grams)
                : Generate(segment, scopeOrdinals, plan.IndexPair, grams);
        }
        finally
        {
            if (rentedGrams is not null)
            {
                ArrayPool<int>.Shared.Return(rentedGrams);
            }
        }
    }

    public static CandidateResult IncludeNonAsciiDocuments(
        MappedSegment segment,
        OrdinalSet scopeOrdinals,
        in CandidateResult candidates)
    {
        using var candidateBits = new PooledBitSet(segment.DocumentCount);
        candidates.Ordinals.CopyTo(candidateBits);
        foreach (var ordinal in scopeOrdinals)
        {
            // ASCII-insensitive postings cannot represent the additional Unicode
            // equivalences of RegexOptions.IgnoreCase, so those documents need verification.
            if ((segment.GetDocument(ordinal).Flags & SegmentDocumentFlags.Ascii) == 0)
            {
                candidateBits.Set((int)ordinal);
            }
        }

        return new CandidateResult(
            OrdinalSet.FromSorted(candidateBits.ToOrdinals()),
            candidates.PostingsBytesRead);
    }

    public static CandidateResult Generate(MappedSegment segment, OrdinalSet scopeOrdinals, IndexRegionPair pair, ReadOnlySpan<int> grams)
    {
        var postingsBytesRead = 0L;
        if (grams.Length == 0)
        {
            return new CandidateResult(scopeOrdinals, 0);
        }

        if (scopeOrdinals.Count * 4 >= segment.DocumentCount)
        {
            using var candidateBits = new PooledBitSet(segment.DocumentCount);
            using var postingsBits = new PooledBitSet(segment.DocumentCount);
            scopeOrdinals.CopyTo(candidateBits);

            for (var i = 0; i < grams.Length; i++)
            {
                if (!segment.TryGetPostings(pair, grams[i], out var postings, out _))
                {
                    return new CandidateResult(OrdinalSet.Empty, postingsBytesRead);
                }

                postingsBytesRead += postings.Length;
                postingsBits.Clear();
                DecodeIntoBitSet(postings, postingsBits);
                candidateBits.And(postingsBits);
            }

            return new CandidateResult(OrdinalSet.FromSorted(candidateBits.ToOrdinals()), postingsBytesRead);
        }

        ReadOnlySpan<uint> current = scopeOrdinals.TryGetSpan(out var explicitOrdinals)
            ? explicitOrdinals
            : scopeOrdinals.ToArray();
        var firstBuffer = ArrayPool<uint>.Shared.Rent(Math.Max(current.Length, 1));
        var secondBuffer = ArrayPool<uint>.Shared.Rent(Math.Max(current.Length, 1));
        try
        {
            for (var i = 0; i < grams.Length; i++)
            {
                if (!segment.TryGetPostings(pair, grams[i], out var postings, out var docFreq))
                {
                    return new CandidateResult(OrdinalSet.Empty, postingsBytesRead);
                }

                postingsBytesRead += postings.Length;
                var destination = (i & 1) == 0 ? firstBuffer : secondBuffer;
                var intersectionCount = PostingsCodec.IntersectSorted(current, postings, docFreq, destination);
                current = destination.AsSpan(0, intersectionCount);
                if (current.Length == 0)
                {
                    return new CandidateResult(OrdinalSet.Empty, postingsBytesRead);
                }

                if (i + 1 < grams.Length && current.Length * 4 >= segment.DocumentCount)
                {
                    using var candidateBits = new PooledBitSet(segment.DocumentCount);
                    using var postingsBits = new PooledBitSet(segment.DocumentCount);
                    for (var currentIndex = 0; currentIndex < current.Length; currentIndex++)
                    {
                        candidateBits.Set((int)current[currentIndex]);
                    }

                    for (var remainingIndex = i + 1; remainingIndex < grams.Length; remainingIndex++)
                    {
                        if (!segment.TryGetPostings(pair, grams[remainingIndex], out var remainingPostings, out _))
                        {
                            return new CandidateResult(OrdinalSet.Empty, postingsBytesRead);
                        }

                        postingsBytesRead += remainingPostings.Length;
                        postingsBits.Clear();
                        DecodeIntoBitSet(remainingPostings, postingsBits);
                        candidateBits.And(postingsBits);
                    }

                    return new CandidateResult(OrdinalSet.FromSorted(candidateBits.ToOrdinals()), postingsBytesRead);
                }
            }

            return new CandidateResult(
                OrdinalSet.FromSorted(current.ToArray()),
                postingsBytesRead);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(firstBuffer);
            ArrayPool<uint>.Shared.Return(secondBuffer);
        }
    }

    private static CandidateResult GenerateUnion(MappedSegment segment, OrdinalSet scopeOrdinals, IndexRegionPair pair, ReadOnlySpan<int> grams)
    {
        var postingsBytesRead = 0L;
        if (grams.Length == 0)
        {
            return new CandidateResult(scopeOrdinals, 0);
        }

        using var candidates = new PooledBitSet(segment.DocumentCount);
        for (var i = 0; i < grams.Length; i++)
        {
            if (!segment.TryGetPostings(pair, grams[i], out var postings, out _))
            {
                continue;
            }

            postingsBytesRead += postings.Length;
            DecodeIntoBitSet(postings, candidates);
        }

        if (!scopeOrdinals.CoversWholeSegment(segment.DocumentCount))
        {
            using var scope = new PooledBitSet(segment.DocumentCount);
            scopeOrdinals.CopyTo(scope);

            candidates.And(scope);
        }

        return new CandidateResult(OrdinalSet.FromSorted(candidates.ToOrdinals()), postingsBytesRead);
    }

    private static CandidateResult GenerateUnionOfIntersections(MappedSegment segment, OrdinalSet scopeOrdinals, IndexRegionPair pair, ReadOnlySpan<int> grams, ReadOnlySpan<int> groupLengths)
    {
        if (grams.Length == 0 || groupLengths.Length == 0)
        {
            return new CandidateResult(scopeOrdinals, 0);
        }

        using var candidates = new PooledBitSet(segment.DocumentCount);
        using var branch = new PooledBitSet(segment.DocumentCount);
        using var postingsBits = new PooledBitSet(segment.DocumentCount);
        var postingsBytesRead = 0L;
        var offset = 0;
        for (var i = 0; i < groupLengths.Length && offset < grams.Length; i++)
        {
            var length = groupLengths[i];
            if (length <= 0 || offset + length > grams.Length)
            {
                break;
            }

            branch.Clear();
            scopeOrdinals.CopyTo(branch);
            var branchExists = true;
            for (var gramIndex = offset; gramIndex < offset + length; gramIndex++)
            {
                if (!segment.TryGetPostings(pair, grams[gramIndex], out var postings, out _))
                {
                    branchExists = false;
                    break;
                }

                postingsBytesRead += postings.Length;
                postingsBits.Clear();
                DecodeIntoBitSet(postings, postingsBits);
                branch.And(postingsBits);
            }

            if (branchExists)
            {
                candidates.Or(branch);
            }

            offset += length;
        }

        return new CandidateResult(OrdinalSet.FromSorted(candidates.ToOrdinals()), postingsBytesRead);
    }

    private static void DecodeIntoBitSet(ReadOnlySpan<byte> postings, PooledBitSet bitSet)
    {
        PostingsCodec.DecodeIntoBitSet(postings, bitSet);
    }

}

internal readonly record struct CandidateResult(
    OrdinalSet Ordinals,
    long PostingsBytesRead);
