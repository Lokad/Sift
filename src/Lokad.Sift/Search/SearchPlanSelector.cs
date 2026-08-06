namespace Lokad.Sift.Search;

internal static class SearchPlanSelector
{
    public static SearchPlanCandidate ChooseBest(List<SearchPlanCandidate> candidates)
    {
        ArgumentOutOfRangeException.ThrowIfZero(candidates.Count);

        var best = candidates[0];
        for (var i = 1; i < candidates.Count; i++)
        {
            var current = candidates[i];
            if (current.EstimatedCost < best.EstimatedCost ||
                (current.EstimatedCost == best.EstimatedCost &&
                 current.EstimatedCandidateDocuments < best.EstimatedCandidateDocuments))
            {
                best = current;
            }
        }

        return best;
    }
}
