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
        if (grams.Length == 0)
        {
            return new CandidateResult(scopeOrdinals, 0);
        }

        if (scopeOrdinals.Count * 4 >= segment.DocumentCount)
        {
            var postingsBytesRead = 0L;
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

        return GenerateSparseIntersection(segment, scopeOrdinals, pair, grams);
    }

    private static CandidateResult GenerateSparseIntersection(
        MappedSegment segment,
        OrdinalSet scopeOrdinals,
        IndexRegionPair pair,
        ReadOnlySpan<int> grams)
    {
        if (!segment.TryGetPostings(pair, grams[0], out var firstPostings, out var firstFrequency))
        {
            return new CandidateResult(OrdinalSet.Empty, 0);
        }
        if (firstFrequency < 0)
        {
            throw new InvalidDataException("Sift postings contain a negative document frequency.");
        }

        var postingsBytesRead = (long)firstPostings.Length;
        var bufferLength = Math.Max(Math.Min(scopeOrdinals.Count, firstFrequency), 1);
        var firstBuffer = ArrayPool<uint>.Shared.Rent(bufferLength);
        uint[]? secondBuffer = null;
        try
        {
            ReadOnlySpan<uint> current;
            var hasExplicitOrdinals = scopeOrdinals.TryGetSpan(out var explicitOrdinals);
            if (!hasExplicitOrdinals && firstFrequency < scopeOrdinals.Count)
            {
                // Decode the shorter posting list once, then restrict it by range.
                var decoded = firstBuffer.AsSpan(0, firstFrequency);
                PostingsCodec.Decode(firstPostings, decoded);
                current = decoded[..scopeOrdinals.FilterSortedRange(decoded)];
            }
            else
            {
                if (!hasExplicitOrdinals)
                {
                    secondBuffer = ArrayPool<uint>.Shared.Rent(Math.Max(scopeOrdinals.Count, 1));
                    scopeOrdinals.CopyTo(secondBuffer);
                    explicitOrdinals = secondBuffer.AsSpan(0, scopeOrdinals.Count);
                }
                var count = PostingsCodec.IntersectSorted(explicitOrdinals, firstPostings, firstFrequency, firstBuffer);
                current = firstBuffer.AsSpan(0, count);
            }

            if (current.Length == 0)
            {
                return new CandidateResult(OrdinalSet.Empty, postingsBytesRead);
            }
            if (grams.Length > 1 && secondBuffer is null)
            {
                secondBuffer = ArrayPool<uint>.Shared.Rent(bufferLength);
            }

            var writeToFirstBuffer = false;
            for (var i = 1; i < grams.Length; i++)
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
            }

            return new CandidateResult(OrdinalSet.FromSorted(current.ToArray()), postingsBytesRead);
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
