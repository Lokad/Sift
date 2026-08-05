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
            return plan.CandidateMode switch
            {
                LiteralCandidateMode.Intersection => Generate(segment, scopeOrdinals, plan.IndexPair, grams),
                LiteralCandidateMode.Union => GenerateUnion(segment, scopeOrdinals, plan.IndexPair, grams),
                LiteralCandidateMode.UnionOfIntersections => GenerateUnionOfIntersections(
                    segment,
                    scopeOrdinals,
                    plan.IndexPair,
                    grams,
                    plan.UnionGroupLengths),
                _ => throw new InvalidOperationException("The literal candidate mode does not use postings.")
            };

            static CandidateResult GenerateUnion(
                MappedSegment segment,
                OrdinalSet scopeOrdinals,
                IndexRegionPair pair,
                ReadOnlySpan<int> grams)
            {
                var postingsBytesRead = 0L;
                using var candidates = new PooledBitSet(segment.DocumentCount);
                for (var i = 0; i < grams.Length; i++)
                {
                    if (!segment.TryGetPostings(pair, grams[i], out var postings, out _))
                    {
                        continue;
                    }

                    postingsBytesRead += postings.Length;
                    PostingsCodec.DecodeIntoBitSet(postings, candidates);
                }

                if (!scopeOrdinals.CoversWholeSegment(segment.DocumentCount))
                {
                    using var scope = new PooledBitSet(segment.DocumentCount);
                    scopeOrdinals.CopyTo(scope);
                    candidates.And(scope);
                }

                return new CandidateResult(OrdinalSet.FromSorted(candidates.ToOrdinals()), postingsBytesRead);
            }

            static CandidateResult GenerateUnionOfIntersections(
                MappedSegment segment,
                OrdinalSet scopeOrdinals,
                IndexRegionPair pair,
                ReadOnlySpan<int> grams,
                ReadOnlySpan<int> groupLengths)
            {
                using var candidates = new PooledBitSet(segment.DocumentCount);
                using var branch = new PooledBitSet(segment.DocumentCount);
                using var postingsBits = new PooledBitSet(segment.DocumentCount);
                var postingsBytesRead = 0L;
                var offset = 0;
                for (var i = 0; i < groupLengths.Length; i++)
                {
                    var length = groupLengths[i];
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
                        PostingsCodec.DecodeIntoBitSet(postings, postingsBits);
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
                PostingsCodec.DecodeIntoBitSet(postings, postingsBits);
                candidateBits.And(postingsBits);
            }

            return new CandidateResult(OrdinalSet.FromSorted(candidateBits.ToOrdinals()), postingsBytesRead);
        }

        var hasExplicitOrdinals = scopeOrdinals.TryGetSpan(out var explicitOrdinals);
        var bufferLength = Math.Max(scopeOrdinals.Count, 1);
        var firstBuffer = ArrayPool<uint>.Shared.Rent(bufferLength);
        uint[]? secondBuffer = null;
        ReadOnlySpan<uint> current;
        var writeToFirstBuffer = true;
        if (hasExplicitOrdinals)
        {
            current = explicitOrdinals;
            if (grams.Length > 1)
            {
                secondBuffer = ArrayPool<uint>.Shared.Rent(bufferLength);
            }
        }
        else
        {
            scopeOrdinals.CopyTo(firstBuffer);
            current = firstBuffer.AsSpan(0, scopeOrdinals.Count);
            secondBuffer = ArrayPool<uint>.Shared.Rent(bufferLength);
            writeToFirstBuffer = false;
        }

        try
        {
            for (var i = 0; i < grams.Length; i++)
            {
                if (!segment.TryGetPostings(pair, grams[i], out var postings, out var docFreq))
                {
                    return new CandidateResult(OrdinalSet.Empty, postingsBytesRead);
                }

                postingsBytesRead += postings.Length;
                var destination = writeToFirstBuffer ? firstBuffer : secondBuffer!;
                var intersectionCount = PostingsCodec.IntersectSorted(current, postings, docFreq, destination);
                current = destination.AsSpan(0, intersectionCount);
                writeToFirstBuffer = !writeToFirstBuffer;
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
                        PostingsCodec.DecodeIntoBitSet(remainingPostings, postingsBits);
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
            if (secondBuffer is not null)
            {
                ArrayPool<uint>.Shared.Return(secondBuffer);
            }
        }
    }

}

internal readonly record struct CandidateResult(
    OrdinalSet Ordinals,
    long PostingsBytesRead);
