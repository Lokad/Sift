using Lokad.Sift;

namespace Lokad.Sift.Search;

internal static class SearchPlanKinds
{
    public static SearchPatternKind GetPatternKind(
        in SearchQuery query,
        in RegexExecutionPlan regexPlan)
    {
        if (query.PatternMode == PatternMode.Literal)
        {
            return SearchPatternKind.Literal;
        }

        return regexPlan.Kind switch
        {
            RegexExecutionKind.ExactLiteral => SearchPatternKind.RegexLiteral,
            RegexExecutionKind.Utf8 => SearchPatternKind.Utf8Regex,
            _ => SearchPatternKind.RegexFallback
        };
    }

    public static SearchCandidateKind GetCandidateKind(int selectedNgramLength, bool usesIndex)
    {
        if (!usesIndex)
        {
            return SearchCandidateKind.DirectScan;
        }

        return selectedNgramLength switch
        {
            2 => SearchCandidateKind.BigramIndex,
            3 => SearchCandidateKind.TrigramIndex,
            _ => SearchCandidateKind.Index
        };
    }

    public static SearchCandidateCombination GetCandidateCombination(
        int selectedNgramCount,
        bool usesIndex,
        bool unionCandidates,
        bool branchUnion)
    {
        if (!usesIndex || selectedNgramCount == 0)
        {
            return SearchCandidateCombination.None;
        }

        if (!unionCandidates)
        {
            return SearchCandidateCombination.Intersection;
        }

        return branchUnion
            ? SearchCandidateCombination.BranchUnion
            : SearchCandidateCombination.Union;
    }

    public static SearchVerifierKind GetVerifierKind(
        in SearchQuery query,
        in RegexExecutionPlan regexPlan)
    {
        if (query.PatternMode == PatternMode.Literal || regexPlan.IsExactLiteral)
        {
            return SearchVerifierKind.Literal;
        }

        return regexPlan.Kind switch
        {
            RegexExecutionKind.Utf8 => SearchVerifierKind.Utf8Regex,
            _ => SearchVerifierKind.RegexFallback
        };
    }
}
