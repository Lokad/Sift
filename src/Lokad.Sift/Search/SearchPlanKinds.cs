using Lokad.Sift;

namespace Lokad.Sift.Search;

internal enum SearchPlanFamily
{
    LiteralDirectScan,
    LiteralIndexed,
    LiteralBigram,
    LiteralTrigram,
    RegexLiteralDirectScan,
    RegexLiteralIndexed,
    RegexLiteralBigram,
    RegexLiteralTrigram,
    Utf8RegexDirectScan,
    Utf8RegexFragmentDirectScan,
    Utf8RegexFragmentIndexed,
    Utf8RegexFragmentBigram,
    Utf8RegexFragmentTrigram,
    Utf8RegexFragmentIndexedUnion,
    Utf8RegexFragmentBigramUnion,
    Utf8RegexFragmentTrigramUnion,
    Utf8RegexFragmentIndexedBranchUnion,
    Utf8RegexFragmentBigramBranchUnion,
    Utf8RegexFragmentTrigramBranchUnion,
    RegexDirectScan,
    RegexFragmentDirectScan,
    RegexFragmentIndexed,
    RegexFragmentBigram,
    RegexFragmentTrigram,
    RegexFragmentIndexedUnion,
    RegexFragmentBigramUnion,
    RegexFragmentTrigramUnion,
    RegexFragmentIndexedBranchUnion,
    RegexFragmentBigramBranchUnion,
    RegexFragmentTrigramBranchUnion,
    PathFirstThenContent
}

internal enum SearchCandidateKind
{
    DirectScan,
    Indexed,
    ContentIndexed,
    ContentBigram,
    ContentTrigram,
    RegexLiteralIndexed,
    RegexLiteralBigram,
    RegexLiteralTrigram,
    Utf8RegexIndexed,
    Utf8RegexBigram,
    Utf8RegexTrigram,
    Utf8RegexIndexedUnion,
    Utf8RegexBigramUnion,
    Utf8RegexTrigramUnion,
    Utf8RegexIndexedBranchUnion,
    Utf8RegexBigramBranchUnion,
    Utf8RegexTrigramBranchUnion,
    RegexIndexed,
    RegexBigram,
    RegexTrigram,
    RegexIndexedUnion,
    RegexBigramUnion,
    RegexTrigramUnion,
    RegexIndexedBranchUnion,
    RegexBigramBranchUnion,
    RegexTrigramBranchUnion,
    PathFirstDirectScan
}

internal enum SearchVerifierKind
{
    LiteralScan,
    IndexedLiteral,
    Utf8RegexLiteral,
    Utf8Regex,
    RegexFallback
}

internal static class SearchPlanKinds
{
    public static string ToLabel(this SearchPlanFamily value)
    {
        return value switch
        {
            SearchPlanFamily.LiteralDirectScan => "LiteralDirectScan",
            SearchPlanFamily.LiteralIndexed => "LiteralIndexed",
            SearchPlanFamily.LiteralBigram => "LiteralBigram",
            SearchPlanFamily.LiteralTrigram => "LiteralTrigram",
            SearchPlanFamily.RegexLiteralDirectScan => "RegexLiteralDirectScan",
            SearchPlanFamily.RegexLiteralIndexed => "RegexLiteralIndexed",
            SearchPlanFamily.RegexLiteralBigram => "RegexLiteralBigram",
            SearchPlanFamily.RegexLiteralTrigram => "RegexLiteralTrigram",
            SearchPlanFamily.Utf8RegexDirectScan => "Utf8RegexDirectScan",
            SearchPlanFamily.Utf8RegexFragmentDirectScan => "Utf8RegexFragmentDirectScan",
            SearchPlanFamily.Utf8RegexFragmentIndexed => "Utf8RegexFragmentIndexed",
            SearchPlanFamily.Utf8RegexFragmentBigram => "Utf8RegexFragmentBigram",
            SearchPlanFamily.Utf8RegexFragmentTrigram => "Utf8RegexFragmentTrigram",
            SearchPlanFamily.Utf8RegexFragmentIndexedUnion => "Utf8RegexFragmentIndexedUnion",
            SearchPlanFamily.Utf8RegexFragmentBigramUnion => "Utf8RegexFragmentBigramUnion",
            SearchPlanFamily.Utf8RegexFragmentTrigramUnion => "Utf8RegexFragmentTrigramUnion",
            SearchPlanFamily.Utf8RegexFragmentIndexedBranchUnion => "Utf8RegexFragmentIndexedBranchUnion",
            SearchPlanFamily.Utf8RegexFragmentBigramBranchUnion => "Utf8RegexFragmentBigramBranchUnion",
            SearchPlanFamily.Utf8RegexFragmentTrigramBranchUnion => "Utf8RegexFragmentTrigramBranchUnion",
            SearchPlanFamily.RegexDirectScan => "RegexDirectScan",
            SearchPlanFamily.RegexFragmentDirectScan => "RegexFragmentDirectScan",
            SearchPlanFamily.RegexFragmentIndexed => "RegexFragmentIndexed",
            SearchPlanFamily.RegexFragmentBigram => "RegexFragmentBigram",
            SearchPlanFamily.RegexFragmentTrigram => "RegexFragmentTrigram",
            SearchPlanFamily.RegexFragmentIndexedUnion => "RegexFragmentIndexedUnion",
            SearchPlanFamily.RegexFragmentBigramUnion => "RegexFragmentBigramUnion",
            SearchPlanFamily.RegexFragmentTrigramUnion => "RegexFragmentTrigramUnion",
            SearchPlanFamily.RegexFragmentIndexedBranchUnion => "RegexFragmentIndexedBranchUnion",
            SearchPlanFamily.RegexFragmentBigramBranchUnion => "RegexFragmentBigramBranchUnion",
            SearchPlanFamily.RegexFragmentTrigramBranchUnion => "RegexFragmentTrigramBranchUnion",
            SearchPlanFamily.PathFirstThenContent => "PathFirstThenContent",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };
    }

    public static string ToLabel(this SearchCandidateKind value)
    {
        return value switch
        {
            SearchCandidateKind.DirectScan => "DirectScan",
            SearchCandidateKind.Indexed => "Indexed",
            SearchCandidateKind.ContentIndexed => "ContentIndexed",
            SearchCandidateKind.ContentBigram => "ContentBigram",
            SearchCandidateKind.ContentTrigram => "ContentTrigram",
            SearchCandidateKind.RegexLiteralIndexed => "RegexLiteralIndexed",
            SearchCandidateKind.RegexLiteralBigram => "RegexLiteralBigram",
            SearchCandidateKind.RegexLiteralTrigram => "RegexLiteralTrigram",
            SearchCandidateKind.Utf8RegexIndexed => "Utf8RegexIndexed",
            SearchCandidateKind.Utf8RegexBigram => "Utf8RegexBigram",
            SearchCandidateKind.Utf8RegexTrigram => "Utf8RegexTrigram",
            SearchCandidateKind.Utf8RegexIndexedUnion => "Utf8RegexIndexedUnion",
            SearchCandidateKind.Utf8RegexBigramUnion => "Utf8RegexBigramUnion",
            SearchCandidateKind.Utf8RegexTrigramUnion => "Utf8RegexTrigramUnion",
            SearchCandidateKind.Utf8RegexIndexedBranchUnion => "Utf8RegexIndexedBranchUnion",
            SearchCandidateKind.Utf8RegexBigramBranchUnion => "Utf8RegexBigramBranchUnion",
            SearchCandidateKind.Utf8RegexTrigramBranchUnion => "Utf8RegexTrigramBranchUnion",
            SearchCandidateKind.RegexIndexed => "RegexIndexed",
            SearchCandidateKind.RegexBigram => "RegexBigram",
            SearchCandidateKind.RegexTrigram => "RegexTrigram",
            SearchCandidateKind.RegexIndexedUnion => "RegexIndexedUnion",
            SearchCandidateKind.RegexBigramUnion => "RegexBigramUnion",
            SearchCandidateKind.RegexTrigramUnion => "RegexTrigramUnion",
            SearchCandidateKind.RegexIndexedBranchUnion => "RegexIndexedBranchUnion",
            SearchCandidateKind.RegexBigramBranchUnion => "RegexBigramBranchUnion",
            SearchCandidateKind.RegexTrigramBranchUnion => "RegexTrigramBranchUnion",
            SearchCandidateKind.PathFirstDirectScan => "PathFirstDirectScan",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };
    }

    public static string ToLabel(this SearchVerifierKind value)
    {
        return value switch
        {
            SearchVerifierKind.LiteralScan => "LiteralScan",
            SearchVerifierKind.IndexedLiteral => "IndexedLiteral",
            SearchVerifierKind.Utf8RegexLiteral => "Utf8RegexLiteral",
            SearchVerifierKind.Utf8Regex => "Utf8Regex",
            SearchVerifierKind.RegexFallback => "RegexFallback",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };
    }

    public static SearchPlanFamily GetDirectScanPlanFamily(bool hasMandatoryFragments, in RegexExecutionPlan regexPlan)
    {
        return regexPlan.Kind switch
        {
            RegexExecutionKind.ExactLiteral => SearchPlanFamily.RegexLiteralDirectScan,
            RegexExecutionKind.Byte => hasMandatoryFragments ? SearchPlanFamily.Utf8RegexFragmentDirectScan : SearchPlanFamily.Utf8RegexDirectScan,
            RegexExecutionKind.Fallback => hasMandatoryFragments ? SearchPlanFamily.RegexFragmentDirectScan : SearchPlanFamily.RegexDirectScan,
            _ => SearchPlanFamily.LiteralDirectScan
        };
    }

    public static SearchCandidateKind GetCandidateKind(in SearchQuery query, int selectedLength, in RegexExecutionPlan regexPlan, bool usesIndex, bool unionCandidates, bool branchUnion)
    {
        if (!usesIndex)
        {
            return SearchCandidateKind.DirectScan;
        }

        if (selectedLength == 0)
        {
            return SearchCandidateKind.Indexed;
        }

        if (query.PatternMode != PatternMode.Regex)
        {
            return selectedLength switch
            {
                2 => SearchCandidateKind.ContentBigram,
                3 => SearchCandidateKind.ContentTrigram,
                _ => SearchCandidateKind.ContentIndexed
            };
        }

        return regexPlan.Kind switch
        {
            RegexExecutionKind.ExactLiteral => GetRegexLiteralCandidateKind(selectedLength),
            RegexExecutionKind.Byte => GetRegexCandidateKind(selectedLength, unionCandidates, branchUnion, SearchCandidateKind.Utf8RegexIndexed, SearchCandidateKind.Utf8RegexBigram, SearchCandidateKind.Utf8RegexTrigram, SearchCandidateKind.Utf8RegexIndexedUnion, SearchCandidateKind.Utf8RegexBigramUnion, SearchCandidateKind.Utf8RegexTrigramUnion, SearchCandidateKind.Utf8RegexIndexedBranchUnion, SearchCandidateKind.Utf8RegexBigramBranchUnion, SearchCandidateKind.Utf8RegexTrigramBranchUnion),
            RegexExecutionKind.Fallback => GetRegexCandidateKind(selectedLength, unionCandidates, branchUnion, SearchCandidateKind.RegexIndexed, SearchCandidateKind.RegexBigram, SearchCandidateKind.RegexTrigram, SearchCandidateKind.RegexIndexedUnion, SearchCandidateKind.RegexBigramUnion, SearchCandidateKind.RegexTrigramUnion, SearchCandidateKind.RegexIndexedBranchUnion, SearchCandidateKind.RegexBigramBranchUnion, SearchCandidateKind.RegexTrigramBranchUnion),
            _ => selectedLength switch
            {
                2 => SearchCandidateKind.ContentBigram,
                3 => SearchCandidateKind.ContentTrigram,
                _ => SearchCandidateKind.ContentIndexed
            }
        };
    }

    public static SearchPlanFamily GetPlanFamily(in SearchQuery query, int selectedLength, bool hasMandatoryFragments, in RegexExecutionPlan regexPlan, bool usesIndex, bool unionCandidates, bool branchUnion)
    {
        if (!usesIndex)
        {
            return query.PatternMode == PatternMode.Regex
                ? GetDirectScanPlanFamily(hasMandatoryFragments, regexPlan)
                : SearchPlanFamily.LiteralDirectScan;
        }

        if (query.PatternMode != PatternMode.Regex)
        {
            return selectedLength switch
            {
                0 => SearchPlanFamily.LiteralIndexed,
                2 => SearchPlanFamily.LiteralBigram,
                3 => SearchPlanFamily.LiteralTrigram,
                _ => SearchPlanFamily.LiteralIndexed
            };
        }

        return regexPlan.Kind switch
        {
            RegexExecutionKind.ExactLiteral => selectedLength switch
            {
                0 => SearchPlanFamily.RegexLiteralIndexed,
                2 => SearchPlanFamily.RegexLiteralBigram,
                3 => SearchPlanFamily.RegexLiteralTrigram,
                _ => SearchPlanFamily.RegexLiteralIndexed
            },
            RegexExecutionKind.Byte => GetRegexPlanFamily(selectedLength, unionCandidates, branchUnion, SearchPlanFamily.Utf8RegexFragmentIndexed, SearchPlanFamily.Utf8RegexFragmentBigram, SearchPlanFamily.Utf8RegexFragmentTrigram, SearchPlanFamily.Utf8RegexFragmentIndexedUnion, SearchPlanFamily.Utf8RegexFragmentBigramUnion, SearchPlanFamily.Utf8RegexFragmentTrigramUnion, SearchPlanFamily.Utf8RegexFragmentIndexedBranchUnion, SearchPlanFamily.Utf8RegexFragmentBigramBranchUnion, SearchPlanFamily.Utf8RegexFragmentTrigramBranchUnion),
            RegexExecutionKind.Fallback => GetRegexPlanFamily(selectedLength, unionCandidates, branchUnion, SearchPlanFamily.RegexFragmentIndexed, SearchPlanFamily.RegexFragmentBigram, SearchPlanFamily.RegexFragmentTrigram, SearchPlanFamily.RegexFragmentIndexedUnion, SearchPlanFamily.RegexFragmentBigramUnion, SearchPlanFamily.RegexFragmentTrigramUnion, SearchPlanFamily.RegexFragmentIndexedBranchUnion, SearchPlanFamily.RegexFragmentBigramBranchUnion, SearchPlanFamily.RegexFragmentTrigramBranchUnion),
            _ => selectedLength switch
            {
                0 => SearchPlanFamily.LiteralIndexed,
                2 => SearchPlanFamily.LiteralBigram,
                3 => SearchPlanFamily.LiteralTrigram,
                _ => SearchPlanFamily.LiteralIndexed
            }
        };
    }

    public static SearchVerifierKind GetVerifierKind(in SearchQuery query, in RegexExecutionPlan regexPlan, bool usesIndex)
    {
        if (regexPlan.IsExactLiteral)
        {
            return SearchVerifierKind.Utf8RegexLiteral;
        }

        if (query.PatternMode == PatternMode.Literal)
        {
            return usesIndex ? SearchVerifierKind.IndexedLiteral : SearchVerifierKind.LiteralScan;
        }

        return regexPlan.Kind switch
        {
            RegexExecutionKind.Byte => SearchVerifierKind.Utf8Regex,
            RegexExecutionKind.Fallback => SearchVerifierKind.RegexFallback,
            _ => SearchVerifierKind.LiteralScan
        };
    }

    private static SearchCandidateKind GetRegexLiteralCandidateKind(int selectedLength)
    {
        return selectedLength switch
        {
            0 => SearchCandidateKind.RegexLiteralIndexed,
            2 => SearchCandidateKind.RegexLiteralBigram,
            3 => SearchCandidateKind.RegexLiteralTrigram,
            _ => SearchCandidateKind.RegexLiteralIndexed
        };
    }

    private static SearchCandidateKind GetRegexCandidateKind(int selectedLength, bool unionCandidates, bool branchUnion, SearchCandidateKind indexed, SearchCandidateKind bigram, SearchCandidateKind trigram, SearchCandidateKind indexedUnion, SearchCandidateKind bigramUnion, SearchCandidateKind trigramUnion, SearchCandidateKind indexedBranchUnion, SearchCandidateKind bigramBranchUnion, SearchCandidateKind trigramBranchUnion)
    {
        if (unionCandidates)
        {
            return (selectedLength, branchUnion) switch
            {
                (2, false) => bigramUnion,
                (3, false) => trigramUnion,
                (2, true) => bigramBranchUnion,
                (3, true) => trigramBranchUnion,
                (_, false) => indexedUnion,
                _ => indexedBranchUnion
            };
        }

        return selectedLength switch
        {
            2 => bigram,
            3 => trigram,
            _ => indexed
        };
    }

    private static SearchPlanFamily GetRegexPlanFamily(int selectedLength, bool unionCandidates, bool branchUnion, SearchPlanFamily indexed, SearchPlanFamily bigram, SearchPlanFamily trigram, SearchPlanFamily indexedUnion, SearchPlanFamily bigramUnion, SearchPlanFamily trigramUnion, SearchPlanFamily indexedBranchUnion, SearchPlanFamily bigramBranchUnion, SearchPlanFamily trigramBranchUnion)
    {
        if (unionCandidates)
        {
            return (selectedLength, branchUnion) switch
            {
                (2, false) => bigramUnion,
                (3, false) => trigramUnion,
                (2, true) => bigramBranchUnion,
                (3, true) => trigramBranchUnion,
                (_, false) => indexedUnion,
                _ => indexedBranchUnion
            };
        }

        return selectedLength switch
        {
            2 => bigram,
            3 => trigram,
            _ => indexed
        };
    }
}
