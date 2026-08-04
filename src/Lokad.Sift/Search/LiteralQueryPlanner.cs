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
        CaseMode resolvedCaseMode,
        SiftOptions options)
    {
        var direct = LiteralPlan.Direct(resolvedCaseMode, scopeDocuments, scopeBytes);
        if (scopeDocuments == 0 || pattern.Length == 0)
        {
            return [direct];
        }

        var indexed = TryCreateIndexedCandidate(segment, scopeDocuments, scopeBytes, pattern, resolvedCaseMode, options);
        return indexed.HasValue
            ? [direct, indexed.Value]
            : [direct];
    }

    public static LiteralPlan Create(
        MappedSegment segment,
        int scopeDocuments,
        long scopeBytes,
        string pattern,
        CaseMode resolvedCaseMode,
        SiftOptions options)
    {
        var candidates = Enumerate(segment, scopeDocuments, scopeBytes, pattern, resolvedCaseMode, options);
        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        var direct = candidates[0];
        var indexed = candidates[1];
        if (indexed.IsMissingGram)
        {
            return indexed;
        }

        var patternBytes = Encoding.UTF8.GetBytes(pattern);
        return ShouldPreferDirectScan(scopeDocuments, scopeBytes, patternBytes.Length, indexed.SelectedGrams, options)
            ? direct
            : indexed;
    }

    private static LiteralPlan? TryCreateIndexedCandidate(
        MappedSegment segment,
        int scopeDocuments,
        long scopeBytes,
        string pattern,
        CaseMode resolvedCaseMode,
        SiftOptions options)
    {
        if (scopeDocuments <= options.DirectScanScopeDocumentsThreshold && scopeBytes <= options.DirectScanScopeBytesThreshold)
        {
            return null;
        }

        var patternBytes = Encoding.UTF8.GetBytes(pattern);
        if (patternBytes.Length < MinIndexedFragmentBytes)
        {
            return null;
        }

        var caseInsensitiveAscii = resolvedCaseMode == CaseMode.Insensitive && patternBytes.AsSpan().IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0;
        if (resolvedCaseMode == CaseMode.Insensitive && !caseInsensitiveAscii)
        {
            return null;
        }

        var gramLength = patternBytes.Length >= 3 ? 3 : 2;
        var pair = SelectIndexRegionPair(gramLength, caseInsensitiveAscii);
        var grams = new List<SelectedGram>();
        for (var i = 0; i <= patternBytes.Length - gramLength && grams.Count < options.MaxSelectedNgrams; i++)
        {
            if (!SegmentIndexBuilder.TryEncodeGram(patternBytes.AsSpan(i, gramLength), caseInsensitiveAscii, out var gram))
            {
                return null;
            }

            if (!segment.TryGetPostings(pair, gram, out var postings, out var docFreq))
            {
                return LiteralPlan.Indexed(resolvedCaseMode, scopeDocuments, scopeBytes, pattern, [], pair, gramLength, isMissing: true);
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
        var selected = TrimSelectedGrams(grams, scopeDocuments, options);
        return LiteralPlan.Indexed(resolvedCaseMode, scopeDocuments, scopeBytes, pattern, selected, pair, gramLength, isMissing: false);
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

        var targetCandidates = GetTargetCandidateDocuments(scopeDocuments, options);
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

    private static int GetTargetCandidateDocuments(int scopeDocuments, SiftOptions options)
    {
        var target = int.MaxValue;
        if (options.DirectScanScopeDocumentsThreshold > 0)
        {
            target = Math.Min(target, options.DirectScanScopeDocumentsThreshold);
        }

        if (options.DirectScanCandidateDensityThreshold > 0)
        {
            target = Math.Min(target, (int)Math.Ceiling(scopeDocuments * options.DirectScanCandidateDensityThreshold));
        }

        return target == int.MaxValue ? target : Math.Max(target, 1);
    }

    private static bool ShouldPreferDirectScan(int scopeDocuments, long scopeBytes, int patternBytes, ReadOnlySpan<SelectedGram> selected, SiftOptions options)
    {
        if (selected.Length == 0)
        {
            return true;
        }

        if (scopeDocuments <= options.DirectScanScopeDocumentsThreshold && scopeBytes <= options.DirectScanScopeBytesThreshold)
        {
            return true;
        }

        var estimatedCandidates = EstimateCandidateUpperBound(scopeDocuments, selected);
        if (options.DirectScanCandidateDensityThreshold > 0 &&
            estimatedCandidates >= Math.Ceiling(scopeDocuments * options.DirectScanCandidateDensityThreshold))
        {
            return true;
        }

        var directScanCost = (double)Math.Max(scopeBytes, scopeDocuments);
        var indexedCost = EstimateIndexedCost(patternBytes, estimatedCandidates, selected);
        return indexedCost >= directScanCost;
    }

    private static double EstimateCandidateUpperBound(int scopeDocuments, ReadOnlySpan<SelectedGram> selected)
    {
        if (scopeDocuments <= 0 || selected.Length == 0)
        {
            return 0;
        }

        var estimate = (double)scopeDocuments;
        for (var i = 0; i < selected.Length; i++)
        {
            var ratio = Math.Clamp((double)selected[i].DocFreq / scopeDocuments, 0d, 1d);
            estimate *= ratio;
        }

        return Math.Max(estimate, 1d);
    }

    private static double EstimateIndexedCost(int patternBytes, double estimatedCandidates, ReadOnlySpan<SelectedGram> selected)
    {
        long postingsBytes = 0;
        for (var i = 0; i < selected.Length; i++)
        {
            postingsBytes += selected[i].PostingsBytes;
        }

        var verifierCost = estimatedCandidates * Math.Max(patternBytes, selected[0].GramLength * 4);
        return postingsBytes + verifierCost;
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
    bool UsesIndex,
    bool UnionCandidates,
    bool IsMissingGram,
    string MandatoryFragment,
    SelectedGram[] SelectedGrams,
    IndexRegionPair IndexPair,
    int GramLength,
    int[]? UnionGroupLengths)
{
    public static LiteralPlan Direct(CaseMode resolvedCaseMode, int scopeDocuments, long scopeBytes)
        => new(resolvedCaseMode, scopeDocuments, scopeBytes, false, false, false, string.Empty, [], default, 0, null);

    public static LiteralPlan Indexed(
        CaseMode resolvedCaseMode,
        int scopeDocuments,
        long scopeBytes,
        string mandatoryFragment,
        SelectedGram[] selectedGrams,
        IndexRegionPair pair,
        int gramLength,
        bool isMissing)
        => Indexed(
            resolvedCaseMode,
            scopeDocuments,
            scopeBytes,
            mandatoryFragment,
            selectedGrams,
            pair,
            gramLength,
            isMissing,
            unionCandidates: false,
            unionGroupLengths: null);

    public static LiteralPlan Indexed(
        CaseMode resolvedCaseMode,
        int scopeDocuments,
        long scopeBytes,
        string mandatoryFragment,
        SelectedGram[] selectedGrams,
        IndexRegionPair pair,
        int gramLength,
        bool isMissing,
        bool unionCandidates)
        => Indexed(
            resolvedCaseMode,
            scopeDocuments,
            scopeBytes,
            mandatoryFragment,
            selectedGrams,
            pair,
            gramLength,
            isMissing,
            unionCandidates,
            unionGroupLengths: null);

    public static LiteralPlan Indexed(
        CaseMode resolvedCaseMode,
        int scopeDocuments,
        long scopeBytes,
        string mandatoryFragment,
        SelectedGram[] selectedGrams,
        IndexRegionPair pair,
        int gramLength,
        bool isMissing,
        bool unionCandidates,
        int[]? unionGroupLengths)
        => new(resolvedCaseMode, scopeDocuments, scopeBytes, true, unionCandidates, isMissing, mandatoryFragment, selectedGrams, pair, gramLength, unionGroupLengths);
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

