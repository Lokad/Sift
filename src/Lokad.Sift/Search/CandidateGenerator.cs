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
            if ((segment.GetDocument(ordinal).Flags & (1 << 1)) == 0)
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
            scopeOrdinals.CopyTo(candidateBits);

            for (var i = 0; i < grams.Length; i++)
            {
                if (!segment.TryGetPostings(pair, grams[i], out var postings, out _))
                {
                    return new CandidateResult(OrdinalSet.Empty, postingsBytesRead);
                }

                postingsBytesRead += postings.Length;
                using var postingsBits = new PooledBitSet(segment.DocumentCount);
                DecodeIntoBitSet(postings, postingsBits);
                candidateBits.And(postingsBits);
            }

            return new CandidateResult(OrdinalSet.FromSorted(candidateBits.ToOrdinals()), postingsBytesRead);
        }

        var current = scopeOrdinals.TryGetSpan(out var explicitOrdinals)
            ? explicitOrdinals
            : scopeOrdinals.ToArray();
        uint[]? currentBuffer = null;
        for (var i = 0; i < grams.Length; i++)
        {
            if (!segment.TryGetPostings(pair, grams[i], out var postings, out var docFreq))
            {
                return new CandidateResult(OrdinalSet.Empty, postingsBytesRead);
            }

            postingsBytesRead += postings.Length;
            currentBuffer = IntersectSortedWithPostings(current, postings, docFreq);
            current = currentBuffer;
            if (current.Length == 0)
            {
                return new CandidateResult(OrdinalSet.Empty, postingsBytesRead);
            }

            if (i + 1 < grams.Length && current.Length * 4 >= segment.DocumentCount)
            {
                using var candidateBits = new PooledBitSet(segment.DocumentCount);
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
                    using var postingsBits = new PooledBitSet(segment.DocumentCount);
                    DecodeIntoBitSet(remainingPostings, postingsBits);
                    candidateBits.And(postingsBits);
                }

                return new CandidateResult(OrdinalSet.FromSorted(candidateBits.ToOrdinals()), postingsBytesRead);
            }
        }

        return new CandidateResult(
            OrdinalSet.FromSorted(currentBuffer ?? []),
            postingsBytesRead);
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
        var postingsBytesRead = 0L;
        var offset = 0;
        for (var i = 0; i < groupLengths.Length && offset < grams.Length; i++)
        {
            var length = groupLengths[i];
            if (length <= 0 || offset + length > grams.Length)
            {
                break;
            }

            var branch = Generate(segment, scopeOrdinals, pair, grams.Slice(offset, length));
            postingsBytesRead += branch.PostingsBytesRead;
            foreach (var ordinal in branch.Ordinals)
            {
                candidates.Set((int)ordinal);
            }

            offset += length;
        }

        return new CandidateResult(OrdinalSet.FromSorted(candidates.ToOrdinals()), postingsBytesRead);
    }

    private static void DecodeIntoBitSet(ReadOnlySpan<byte> postings, PooledBitSet bitSet)
    {
        PostingsCodec.DecodeIntoBitSet(postings, bitSet);
    }

    private static uint[] IntersectSortedWithPostings(ReadOnlySpan<uint> left, ReadOnlySpan<byte> postings, int docFreq)
    {
        return PostingsCodec.IntersectSorted(left, postings, docFreq);
    }

    public static uint[] Decode(ReadOnlySpan<byte> postings)
    {
        return PostingsCodec.Decode(postings);
    }

    internal static uint[] IntersectSorted(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return [];
        }

        if (left.Length > right.Length)
        {
            return IntersectSorted(right, left);
        }

        return right.Length >= left.Length * 8
            ? GallopingIntersect(left, right)
            : MergeIntersect(left, right);
    }

    private static uint[] MergeIntersect(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
    {
        var result = new uint[Math.Min(left.Length, right.Length)];
        var count = 0;
        var i = 0;
        var j = 0;
        while (i < left.Length && j < right.Length)
        {
            var leftValue = left[i];
            var rightValue = right[j];
            if (leftValue == rightValue)
            {
                result[count++] = leftValue;
                i++;
                j++;
            }
            else if (leftValue < rightValue)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        Array.Resize(ref result, count);
        return result;
    }

    private static uint[] GallopingIntersect(ReadOnlySpan<uint> small, ReadOnlySpan<uint> large)
    {
        var result = new uint[small.Length];
        var count = 0;
        var largeStart = 0;
        for (var i = 0; i < small.Length && largeStart < large.Length; i++)
        {
            var target = small[i];
            var index = LowerBound(large, largeStart, target);
            if (index >= large.Length)
            {
                break;
            }

            if (large[index] == target)
            {
                result[count++] = target;
                largeStart = index + 1;
            }
            else
            {
                largeStart = index;
            }
        }

        Array.Resize(ref result, count);
        return result;
    }

    private static int LowerBound(ReadOnlySpan<uint> values, int start, uint target)
    {
        var low = start;
        var high = values.Length;
        while (low < high)
        {
            var mid = low + ((high - low) >> 1);
            if (values[mid] < target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}

internal readonly record struct CandidateResult(
    OrdinalSet Ordinals,
    long PostingsBytesRead);
