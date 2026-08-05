using System.Text;
using Lokad.Sift;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal static class LiteralQueryPlanner
{
    private const int MinIndexedFragmentBytes = 2;

    public static LiteralPlan[] Enumerate(
        MappedSegment segment,
        int scopeDocuments,
        long scopeBytes,
        string pattern,
        ReadOnlySpan<byte> patternBytes,
        CaseMode resolvedCaseMode,
        bool selectAllGrams,
        SiftOptions options)
    {
        var direct = LiteralPlan.Direct(resolvedCaseMode, scopeDocuments, scopeBytes);
        if (scopeDocuments == 0 || pattern.Length == 0)
        {
            return [direct];
        }

        var indexed = TryCreateIndexedCandidate(segment, scopeDocuments, scopeBytes, pattern, patternBytes, resolvedCaseMode, selectAllGrams, options);
        return indexed.HasValue
            ? [direct, indexed.Value]
            : [direct];
    }

    private static LiteralPlan? TryCreateIndexedCandidate(
        MappedSegment segment,
        int scopeDocuments,
        long scopeBytes,
        string pattern,
        ReadOnlySpan<byte> patternBytes,
        CaseMode resolvedCaseMode,
        bool selectAllGrams,
        SiftOptions options)
    {
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
                return LiteralPlan.Missing(resolvedCaseMode, scopeDocuments, scopeBytes, pattern, pair, gramLength);
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
        return LiteralPlan.Intersection(resolvedCaseMode, scopeDocuments, scopeBytes, pattern, selected, pair, gramLength);
    }

    private static SelectedGram[] TrimSelectedGrams(List<SelectedGram> grams, int scopeDocuments, SiftOptions options)
    {
        if (grams.Count == 0)
        {
            return [];
        }

        if (scopeDocuments <= 0)
        {
            return [grams[0]];
        }

        var targetCandidates = SearchPlanner.GetTargetCandidateDocuments(scopeDocuments, options);
        if (targetCandidates == int.MaxValue)
        {
            return grams.ToArray();
        }

        var selected = new List<SelectedGram>(Math.Min(grams.Count, options.MaxSelectedNgrams));
        var estimatedCandidates = (double)scopeDocuments;
        foreach (var gram in grams)
        {
            if (selected.Count >= options.MaxSelectedNgrams)
            {
                break;
            }

            selected.Add(gram);
            var ratio = Math.Clamp((double)gram.DocFreq / scopeDocuments, 0d, 1d);
            estimatedCandidates *= ratio;
            if (estimatedCandidates <= targetCandidates)
            {
                break;
            }
        }

        return [.. selected];
    }

    private static IndexRegionPair SelectIndexRegionPair(int gramLength, bool caseInsensitiveAscii)
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

internal readonly record struct LiteralPlan(
    CaseMode ResolvedCaseMode,
    int ScopeDocuments,
    long ScopeBytes,
    LiteralCandidateMode CandidateMode,
    string MandatoryFragment,
    SelectedGram[] SelectedGrams,
    IndexRegionPair IndexPair,
    int GramLength,
    int[]? UnionGroupLengths)
{
    public bool UsesIndex => CandidateMode != LiteralCandidateMode.Direct;
    public bool UnionCandidates => CandidateMode is LiteralCandidateMode.Union or LiteralCandidateMode.UnionOfIntersections;
    public bool IsMissingGram => CandidateMode == LiteralCandidateMode.Missing;

    public static LiteralPlan Direct(CaseMode resolvedCaseMode, int scopeDocuments, long scopeBytes)
        => new(resolvedCaseMode, scopeDocuments, scopeBytes, LiteralCandidateMode.Direct, string.Empty, [], default, 0, null);

    public static LiteralPlan Missing(
        CaseMode resolvedCaseMode,
        int scopeDocuments,
        long scopeBytes,
        string mandatoryFragment,
        IndexRegionPair pair,
        int gramLength)
        => new(resolvedCaseMode, scopeDocuments, scopeBytes, LiteralCandidateMode.Missing, mandatoryFragment, [], pair, gramLength, null);

    public static LiteralPlan Intersection(
        CaseMode resolvedCaseMode,
        int scopeDocuments,
        long scopeBytes,
        string mandatoryFragment,
        SelectedGram[] selectedGrams,
        IndexRegionPair pair,
        int gramLength)
        => new(resolvedCaseMode, scopeDocuments, scopeBytes, LiteralCandidateMode.Intersection, mandatoryFragment, selectedGrams, pair, gramLength, null);

    public static LiteralPlan Union(
        CaseMode resolvedCaseMode,
        int scopeDocuments,
        long scopeBytes,
        string mandatoryFragment,
        SelectedGram[] selectedGrams,
        IndexRegionPair pair,
        int gramLength)
        => new(resolvedCaseMode, scopeDocuments, scopeBytes, LiteralCandidateMode.Union, mandatoryFragment, selectedGrams, pair, gramLength, null);

    public static LiteralPlan UnionOfIntersections(
        CaseMode resolvedCaseMode,
        int scopeDocuments,
        long scopeBytes,
        string mandatoryFragment,
        SelectedGram[] selectedGrams,
        IndexRegionPair pair,
        int gramLength,
        int[] unionGroupLengths)
        => new(resolvedCaseMode, scopeDocuments, scopeBytes, LiteralCandidateMode.UnionOfIntersections, mandatoryFragment, selectedGrams, pair, gramLength, unionGroupLengths);
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
    int PostingsBytes)
{
    public string Text
    {
        get
        {
            Span<byte> bytes = stackalloc byte[GramLength];
            var value = Gram;
            for (var i = GramLength - 1; i >= 0; i--)
            {
                bytes[i] = (byte)(value & 0xFF);
                value >>= 8;
            }

            return Encoding.UTF8.GetString(bytes);
        }
    }
}

