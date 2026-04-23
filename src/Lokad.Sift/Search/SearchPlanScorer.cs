namespace Lokad.Sift.Search;

internal static class SearchPlanScorer
{
    public static List<ScoredSearchPlanCandidate> Score(List<SearchPlanCandidate> candidates)
    {
        var scored = new List<ScoredSearchPlanCandidate>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            scored.Add(new ScoredSearchPlanCandidate(candidates[i]));
        }

        return scored;
    }

    public static SearchPlanCandidate ChooseBest(List<ScoredSearchPlanCandidate> candidates)
    {
        ArgumentOutOfRangeException.ThrowIfZero(candidates.Count);

        var best = candidates[0];
        for (var i = 1; i < candidates.Count; i++)
        {
            var current = candidates[i];
            if (current.EstimatedCost < best.EstimatedCost ||
                (current.EstimatedCost == best.EstimatedCost && current.EstimatedCandidateDocuments < best.EstimatedCandidateDocuments))
            {
                best = current;
            }
        }

        return best.Candidate;
    }
}

internal readonly record struct ScoredSearchPlanCandidate(
    SearchPlanCandidate Candidate)
{
    public double EstimatedCost => Candidate.EstimatedCost;

    public double EstimatedCandidateDocuments => Candidate.EstimatedCandidateDocuments;
}
