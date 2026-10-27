namespace Lokad.Sift;

/// <summary>A possible matching document, without verified match coordinates.</summary>
/// <remarks>
/// The key owns its path. Content is borrowed from the producing snapshot and must
/// not be accessed after that snapshot is disposed. Already returned content remains
/// valid across overlay updates. Candidates require exact caller verification.
/// </remarks>
public readonly record struct CandidateDocument(DocumentKey Key, ReadOnlyMemory<byte> Content);

public interface IDocumentCandidateCollector
{
    /// <summary>Consumes one candidate synchronously; false stops the visit.</summary>
    /// <remarks>
    /// Callbacks run in traversal order. They must not dispose the producing snapshot,
    /// or update, export or commit its overlay before returning. Content may be retained
    /// only within its documented snapshot lifetime.
    /// </remarks>
    bool OnCandidate(in CandidateDocument document);
}

/// <summary>Bounds document-candidate traversal, independently of search hit limits.</summary>
public sealed record CandidateVisitOptions
{
    public int MaxDocuments { get; init; } = int.MaxValue;
    /// <summary>Maximum sum of complete content lengths offered to the collector.</summary>
    /// <remarks>This is a work budget, not measured I/O or bytes scanned by Sift.</remarks>
    public long MaxContentBytes { get; init; } = long.MaxValue;
    public long MaxElapsedMilliseconds { get; init; } = 30_000;
}

public enum CandidateVisitStopReason
{
    None,
    MaxDocuments,
    MaxContentBytes,
    MaxElapsedTime,
    CollectorStopped
}

/// <summary>Counts offered candidates, not verified matches.</summary>
/// <remarks>A limit or collector stop is incomplete even if it coincides with the final document.</remarks>
public readonly record struct CandidateVisitStats(
    int VisitedDocuments,
    long ContentBytes,
    long ElapsedMilliseconds,
    CandidateVisitStopReason StopReason)
{
    public bool Completed => StopReason == CandidateVisitStopReason.None;
}
