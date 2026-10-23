using Lokad.Sift;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal static class LiteralQueryPlanner
{
    private const int MinIndexedFragmentBytes = 2;

    public static LiteralPlan? TryCreateIndexedCandidate(
        MappedSegment segment,
        int scopeDocuments,
        long scopeBytes,
        string pattern,
        ReadOnlySpan<byte> patternBytes,
        CaseMode resolvedCaseMode,
        bool selectAllGrams,
        SiftOptions options)
    {
        if (scopeDocuments <= 0 || pattern.Length == 0)
        {
            return null;
        }

        if (scopeDocuments <= options.DirectScanScopeDocumentsThreshold && scopeBytes <= options.DirectScanScopeBytesThreshold)
        {
            return null;
        }

        if (patternBytes.Length < MinIndexedFragmentBytes)
        {
            return null;
        }

        var caseInsensitiveAscii = resolvedCaseMode == CaseMode.Insensitive && patternBytes.IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0;
        if (resolvedCaseMode == CaseMode.Insensitive && !caseInsensitiveAscii)
        {
            return null;
        }

        var gramLength = patternBytes.Length >= 3 ? 3 : 2;
        var pair = SelectIndexRegionPair(gramLength, caseInsensitiveAscii);
        var grams = new List<SelectedGram>();
        for (var i = 0; i <= patternBytes.Length - gramLength && grams.Count < options.MaxSelectedNgrams; i++)
        {
            if (!SegmentIndexBuilder.TryEncodeGram(patternBytes.Slice(i, gramLength), caseInsensitiveAscii, out var gram))
            {
                return null;
            }

            if (!segment.TryGetPostings(pair, gram, out var postings, out var docFreq))
            {
                return LiteralPlan.Missing(
                    scopeDocuments,
                    scopeBytes,
                    patternBytes.Length,
                    pair,
                    gramLength);
            }

            var seen = false;
            for (var existingIndex = 0; existingIndex < grams.Count; existingIndex++)
            {
                if (grams[existingIndex].Gram == gram)
                {
                    seen = true;
                    break;
                }
            }

            if (seen)
            {
                continue;
            }

            grams.Add(new SelectedGram(gram, gramLength, docFreq, postings.Length));
        }

        if (grams.Count == 0)
        {
            return null;
        }

        grams.Sort(static (left, right) => left.DocFreq.CompareTo(right.DocFreq));
        var selected = selectAllGrams
            ? grams.ToArray()
            : TrimSelectedGrams(grams, scopeDocuments, options);
        return LiteralPlan.Intersection(
            scopeDocuments,
            scopeBytes,
            patternBytes.Length,
            selected,
            pair,
            gramLength);

        static SelectedGram[] TrimSelectedGrams(List<SelectedGram> grams, int scopeDocuments, SiftOptions options)
        {
            var targetCandidates = SearchPlanner.GetTargetCandidateDocuments(scopeDocuments, options);
            if (targetCandidates == int.MaxValue)
            {
                return grams.ToArray();
            }

            var selectedCount = 0;
            // Match the planner's independence heuristic when choosing how many
            // grams to retain; this estimate is not a bound on actual candidates.
            var estimatedCandidates = (double)scopeDocuments;
            foreach (var gram in grams)
            {
                selectedCount++;
                var ratio = Math.Clamp((double)gram.DocFreq / scopeDocuments, 0d, 1d);
                estimatedCandidates *= ratio;
                if (estimatedCandidates <= targetCandidates)
                {
                    break;
                }
            }

            var selected = new SelectedGram[selectedCount];
            grams.CopyTo(0, selected, 0, selectedCount);
            return selected;
        }

        static IndexRegionPair SelectIndexRegionPair(int gramLength, bool caseInsensitiveAscii)
        {
            return gramLength == 3
                ? caseInsensitiveAscii
                    ? new IndexRegionPair(SegmentRegionId.ContentTriIcLex, SegmentRegionId.ContentTriIcPost)
                    : new IndexRegionPair(SegmentRegionId.ContentTriCsLex, SegmentRegionId.ContentTriCsPost)
                : caseInsensitiveAscii
                    ? new IndexRegionPair(SegmentRegionId.ContentBiIcLex, SegmentRegionId.ContentBiIcPost)
                    : new IndexRegionPair(SegmentRegionId.ContentBiCsLex, SegmentRegionId.ContentBiCsPost);
        }
    }

}

internal readonly struct LiteralPlan
{
    private LiteralPlan(
        int scopeDocuments,
        long scopeBytes,
        LiteralCandidateMode candidateMode,
        int seedByteLength,
        SelectedGram[] selectedGrams,
        IndexRegionPair indexPair,
        int gramLength,
        int[] unionGroupLengths)
    {
        ScopeDocuments = scopeDocuments;
        ScopeBytes = scopeBytes;
        CandidateMode = candidateMode;
        SeedByteLength = seedByteLength;
        SelectedGrams = selectedGrams;
        IndexPair = indexPair;
        GramLength = gramLength;
        UnionGroupLengths = unionGroupLengths;
    }

    public int ScopeDocuments { get; }
    public long ScopeBytes { get; }
    public LiteralCandidateMode CandidateMode { get; }
    public int SeedByteLength { get; }
    public SelectedGram[] SelectedGrams { get; }
    public IndexRegionPair IndexPair { get; }
    public int GramLength { get; }
    public int[] UnionGroupLengths { get; }
    public bool UsesIndex => CandidateMode != LiteralCandidateMode.Direct;
    public bool IsMissingGram => CandidateMode == LiteralCandidateMode.Missing;

    public static LiteralPlan Direct(int scopeDocuments, long scopeBytes)
        => new(scopeDocuments, scopeBytes, LiteralCandidateMode.Direct, 0, [], default, 0, []);

    public static LiteralPlan Missing(
        int scopeDocuments,
        long scopeBytes,
        int seedByteLength,
        IndexRegionPair pair,
        int gramLength)
        => new(scopeDocuments, scopeBytes, LiteralCandidateMode.Missing, seedByteLength, [], pair, gramLength, []);

    public static LiteralPlan Intersection(
        int scopeDocuments,
        long scopeBytes,
        int seedByteLength,
        SelectedGram[] selectedGrams,
        IndexRegionPair pair,
        int gramLength)
    {
        ArgumentOutOfRangeException.ThrowIfZero(selectedGrams.Length);
        return new(scopeDocuments, scopeBytes, LiteralCandidateMode.Intersection, seedByteLength, selectedGrams, pair, gramLength, []);
    }

    public static LiteralPlan Union(
        int scopeDocuments,
        long scopeBytes,
        int seedByteLength,
        SelectedGram[] selectedGrams,
        IndexRegionPair pair,
        int gramLength)
    {
        ArgumentOutOfRangeException.ThrowIfZero(selectedGrams.Length);
        return new(scopeDocuments, scopeBytes, LiteralCandidateMode.Union, seedByteLength, selectedGrams, pair, gramLength, []);
    }

    public static LiteralPlan UnionOfIntersections(
        int scopeDocuments,
        long scopeBytes,
        int seedByteLength,
        SelectedGram[] selectedGrams,
        IndexRegionPair pair,
        int gramLength,
        int[] unionGroupLengths)
    {
        var selectedGramCount = 0;
        for (var i = 0; i < unionGroupLengths.Length; i++)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unionGroupLengths[i]);
            selectedGramCount = checked(selectedGramCount + unionGroupLengths[i]);
        }

        if (selectedGramCount != selectedGrams.Length)
        {
            throw new ArgumentException("Union group lengths must cover every selected gram.", nameof(unionGroupLengths));
        }

        return new(scopeDocuments, scopeBytes, LiteralCandidateMode.UnionOfIntersections, seedByteLength, selectedGrams, pair, gramLength, unionGroupLengths);
    }
}

internal enum LiteralCandidateMode
{
    Direct,
    Intersection,
    Union,
    UnionOfIntersections,
    Missing
}

internal readonly record struct SelectedGram(
    int Gram,
    int GramLength,
    int DocFreq,
    int PostingsBytes);

