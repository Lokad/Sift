namespace Lokad.Sift.Search;

internal static class CandidateVisit
{
    public static CandidateVisitStats Run(IIndexSnapshot snapshot, SearchQuery query, PathFilter filter,
        IDocumentCandidateCollector collector, CandidateVisitOptions options,
        CancellationToken cancellationToken, bool useIndex)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxDocuments <= 0 || options.MaxContentBytes <= 0 || options.MaxElapsedMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Candidate limits must be positive.");
        cancellationToken.ThrowIfCancellationRequested();
        var watch = ValueStopwatch.StartNew();
        var planningQuery = query with
        {
            CandidatesOnly = useIndex,
            MaxHits = int.MaxValue,
            MaxMatchedDocuments = int.MaxValue,
            MaxHitsPerDocument = 1,
            MaxBytesScanned = long.MaxValue,
            MaxElapsedMilliseconds = options.MaxElapsedMilliseconds,
            RegexTimeoutPolicy = RegexTimeoutPolicy.Throw
        };
        var bridge = new Collector(snapshot, collector, options, cancellationToken);
        if (!useIndex)
        {
            // Existing external implementations cannot expose their postings. Validate
            // the planning query, then use a sound full-scope listing, not an exact
            // whole-document regex that could reject a field-relative anchor.
            snapshot.Explain(planningQuery, filter);
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = options.MaxElapsedMilliseconds - watch.ElapsedMilliseconds;
            if (remaining <= 0)
                return bridge.Stats(watch, CandidateVisitStopReason.MaxElapsedTime);
            planningQuery = new SearchQuery(string.Empty)
            {
                MaxHits = int.MaxValue,
                MaxMatchedDocuments = int.MaxValue,
                MaxHitsPerDocument = 1,
                MaxElapsedMilliseconds = remaining
            };
        }
        var result = snapshot.Search(planningQuery, filter, bridge, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return bridge.Stats(watch, result.StopReason switch
        {
            SearchStopReason.None => CandidateVisitStopReason.None,
            SearchStopReason.MaxHits or SearchStopReason.MaxMatchedDocuments => CandidateVisitStopReason.MaxDocuments,
            SearchStopReason.MaxBytesScanned => CandidateVisitStopReason.MaxContentBytes,
            SearchStopReason.MaxElapsedTime => CandidateVisitStopReason.MaxElapsedTime,
            SearchStopReason.CollectorStopped => bridge.StopReason,
            _ => throw new InvalidOperationException("Candidate traversal stopped at an unexpected search limit.")
        });
    }

    private sealed class Collector(IIndexSnapshot snapshot, IDocumentCandidateCollector inner,
        CandidateVisitOptions options, CancellationToken cancellationToken) : IHitCollector
    {
        private int _visited;
        private long _bytes;
        public CandidateVisitStopReason StopReason { get; private set; } = CandidateVisitStopReason.CollectorStopped;

        public bool OnHit(in RawHit hit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = snapshot.GetDocumentBytes(hit);
            if (content.Length > options.MaxContentBytes - _bytes)
            {
                StopReason = CandidateVisitStopReason.MaxContentBytes;
                return false;
            }
            var document = new CandidateDocument(snapshot.GetDocumentKey(hit), content);
            _visited++;
            _bytes += content.Length;
            var keepGoing = inner.OnCandidate(document);
            cancellationToken.ThrowIfCancellationRequested();
            if (!keepGoing) return false;
            if (_visited >= options.MaxDocuments)
            {
                StopReason = CandidateVisitStopReason.MaxDocuments;
                return false;
            }
            return true;
        }

        public CandidateVisitStats Stats(ValueStopwatch watch, CandidateVisitStopReason reason) =>
            new(_visited, _bytes, watch.ElapsedMilliseconds, reason);
    }
}
