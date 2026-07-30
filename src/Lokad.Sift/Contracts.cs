namespace Lokad.Sift;

public enum PatternMode
{
    Literal,
    Regex
}

public enum CaseMode
{
    Sensitive,
    Insensitive,
    Smart
}

public enum SearchStopReason
{
    None,
    MaxHits,
    MaxDocuments,
    MaxBytesScanned,
    RegexTimeout,
    Cancelled
}

public enum TimeoutPolicy
{
    Default,
    Throw,
    StopSearch
}

public readonly record struct SearchQuery(
    string Pattern,
    PatternMode PatternMode = PatternMode.Regex,
    CaseMode CaseMode = CaseMode.Smart,
    bool WholeWord = false,
    bool WholeLine = false,
    bool Multiline = false,
    bool DotAll = false,
    int MaxHits = 10_000,
    int MaxDocuments = int.MaxValue,
    long MaxBytesScanned = long.MaxValue,
    TimeoutPolicy TimeoutPolicy = TimeoutPolicy.StopSearch);

public readonly record struct SearchOptions(
    int SegmentParallelism = 1);

public readonly record struct PathFilter(
    string? PathPrefix = null,
    string[]? IncludeGlobs = null,
    string[]? ExcludeGlobs = null,
    string? PathRegex = null);

public readonly record struct RawHit(
    ulong SegmentId,
    uint DocumentOrdinal,
    int StartByte,
    int EndByte,
    int StartLine,
    int EndLine);

public readonly record struct DocumentKey(string Path);

public readonly record struct SubmittedDocument(
    string Path,
    ReadOnlyMemory<byte> Content,
    DateTimeOffset? LastModifiedUtc = null);

public sealed class SiftOptions
{
    public int MaxDocumentBytes { get; init; } = 32 * 1024 * 1024;
    public int TargetSegmentDocumentCount { get; init; } = 50_000;
    public long TargetSegmentContentBytes { get; init; } = 64L * 1024 * 1024;
    public int DirectScanScopeDocumentsThreshold { get; init; } = 8192;
    public long DirectScanScopeBytesThreshold { get; init; } = 64L * 1024 * 1024;
    public double DirectScanCandidateDensityThreshold { get; init; } = 0.85;
    public int MaxSelectedNgrams { get; init; } = 24;
}

public readonly record struct SelectedNGram(string Text, int Length, int DocFreq);

public readonly record struct PlannerAlternative(
    string PlanFamily,
    string CandidateKind,
    string VerifierKind,
    bool UsesIndex,
    double EstimatedDirectScanCost,
    double? EstimatedIndexedCost,
    double EstimatedCandidateDocuments,
    double EstimatedCost,
    int SegmentCount);

public readonly record struct SearchPlan(
    string PatternKind,
    CaseMode ResolvedCaseMode,
    int ScopeDocumentCount,
    long ScopeByteCount,
    string[] MandatoryFragments,
    string[] AdvisorySeeds,
    SelectedNGram[] SelectedNgrams,
    bool UsesPathPrefixFilter,
    bool UsesPathRegexFilter,
    bool UsesIncludeGlobs,
    bool UsesExcludeGlobs,
    bool UsesPathIndex,
    bool UsesIndex,
    bool CanUseLiteralVerifier,
    bool CanUseNonBacktracking,
    bool NeedsUnicodeDecode,
    bool MayRequireWholeDocumentVerification,
    double EstimatedPathFilterCost,
    double EstimatedDirectScanCost,
    double? EstimatedIndexedCost,
    double EstimatedCandidateDocuments,
    double EstimatedCost,
    string PlanFamily,
    string CandidateKind,
    string VerifierKind,
    string? RunnerUpPlanFamily,
    string? RunnerUpCandidateKind,
    string? RunnerUpVerifierKind,
    double? RunnerUpEstimatedCostDelta,
    TimeoutPolicy TimeoutPolicy,
    PlannerAlternative[] PlannerAlternatives);

public readonly record struct SearchStats(
    long ElapsedMilliseconds,
    int ScopeDocuments,
    long ScopeBytes,
    long PostingsBytesRead,
    int CandidateDocumentsAfterIndex,
    int CandidateDocumentsAfterPrecheck,
    int VerifiedDocuments,
    long BytesScanned,
    long DecodedChars,
    int RegexInvocations,
    int Utf8RegexInvocations,
    int FallbackRegexInvocations,
    int NonBacktrackingRegexInvocations,
    int Utf8RegexMatchedDocuments,
    long Utf8RegexMatchesEnumerated,
    long Utf8RegexCacheHits,
    long Utf8RegexCacheMisses,
    int HitsReturned,
    SearchStopReason StopReason);

public readonly record struct BuildStats(
    int SubmittedDocuments,
    int IndexedDocuments,
    int RejectedDocuments,
    int SegmentCount,
    long SubmittedBytes,
    long ContentBytes,
    long RejectedBytes,
    long ElapsedMilliseconds,
    long PeakManagedBytes);

public readonly record struct UpdateStats(
    int SubmittedUpserts,
    int UpsertedDocuments,
    int RejectedUpserts,
    int DeletedDocuments,
    int PublishedSegments,
    long SubmittedBytes,
    long ContentBytes,
    long RejectedBytes,
    long ElapsedMilliseconds);

public sealed record CompactOptions
{
    public CompactOptions()
        : this(
            MaxMergedSegments: 16,
            DeadFractionThreshold: 0.20,
            SegmentCountTrigger: 16,
            MetadataBloatDeadOrdinalsThreshold: 1024,
            MaxInputSegmentBytes: 1L * 1024 * 1024 * 1024)
    {
    }

    public CompactOptions(
        int MaxMergedSegments = 16,
        double DeadFractionThreshold = 0.20,
        int SegmentCountTrigger = 16,
        int MetadataBloatDeadOrdinalsThreshold = 1024,
        long MaxInputSegmentBytes = 1L * 1024 * 1024 * 1024)
    {
        this.MaxMergedSegments = MaxMergedSegments;
        this.DeadFractionThreshold = DeadFractionThreshold;
        this.SegmentCountTrigger = SegmentCountTrigger;
        this.MetadataBloatDeadOrdinalsThreshold = MetadataBloatDeadOrdinalsThreshold;
        this.MaxInputSegmentBytes = MaxInputSegmentBytes;
    }

    public int MaxMergedSegments { get; init; }
    public double DeadFractionThreshold { get; init; }
    public int SegmentCountTrigger { get; init; }
    public int MetadataBloatDeadOrdinalsThreshold { get; init; }
    // Applies to physical packs; one larger debt-bearing segment remains reclaimable.
    public long MaxInputSegmentBytes { get; init; }
}

public readonly record struct CompactStats(
    int InputSegments,
    int OutputSegments,
    int MergedDocuments,
    long ContentBytes,
    long ElapsedMilliseconds,
    long PeakManagedBytes);

public enum SiftActivity
{
    Idle,
    Build,
    Update,
    Compact
}

public readonly record struct ActivityStats(
    bool IsWriteBusy,
    SiftActivity CurrentActivity);

public readonly record struct MaintenanceStats(
    ulong Generation,
    int SegmentCount,
    int TotalDocuments,
    int LiveDocuments,
    int DeadDocuments,
    double DeadDocumentFraction,
    long ContentBytes,
    long EstimatedLiveContentBytes,
    long EstimatedReclaimableContentBytes,
    long DeadOrdinalEntries,
    int SegmentsWithDeletions,
    int SmallSegmentCount);

public readonly record struct StorageStats(
    // Currently reachable packs that are also present on disk.
    int ManifestSegmentCount,
    // On-disk lengths of those packs; this excludes manifest metadata.
    long ManifestSegmentBytes,
    // All well-named directories containing a pack, including unreachable packs.
    int PhysicalSegmentCount,
    // Segment-pack footprint only; temporary and lifecycle files are excluded.
    long PhysicalSegmentBytes,
    // Packs unreachable from the current manifest, regardless of why they remain.
    int RetiredSegmentCount,
    // On-disk lengths of the unreachable packs.
    long RetiredSegmentBytes,
    // Incomplete publication files, not retired packs awaiting reclamation.
    long TemporaryBytes,
    // Non-current generations held open by at least one reader.
    int LeasedGenerationCount,
    // Lowest held generation, or null when no superseded snapshot remains.
    ulong? OldestLeasedGeneration,
    // Retired packs retained solely because a live snapshot can still read them.
    int SnapshotDeferredSegmentCount,
    // On-disk lengths of the snapshot-protected packs.
    long SnapshotDeferredSegmentBytes,
    // Retired packs proven unreachable but not removed by the latest sweep.
    int PendingDeletionSegmentCount,
    // On-disk lengths still eligible for deletion.
    long PendingDeletionSegmentBytes,
    // Retired packs whose reachability cannot be proven from lifecycle metadata.
    int BlockedReclamationSegmentCount,
    // On-disk lengths withheld from deletion because reachability is uncertain.
    long BlockedReclamationSegmentBytes,
    // True when metadata uncertainty prevents destructive reclamation.
    bool IsReclamationBlocked);

public readonly record struct MaterializedHit(
    string Path,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string MatchText,
    string[] BeforeContext,
    string[] AfterContext);

public interface IHitCollector
{
    bool OnHit(in RawHit hit);
}

public interface IDocumentSource
{
    bool TryReadNext(out SubmittedDocument document);
}

public interface IIndexSnapshot : IDisposable
{
    SearchPlan Explain(in SearchQuery query, in PathFilter pathFilter);
    SearchStats Search(in SearchQuery query, in PathFilter pathFilter, IHitCollector collector, CancellationToken cancellationToken = default);
    MaterializedHit Materialize(in RawHit hit, int contextBefore, int contextAfter);
    bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content);
}

public interface IOverlaySnapshot : IIndexSnapshot
{
    Task<UpdateStats> Apply(IDocumentSource upserts, ReadOnlySpan<DocumentKey> deletions, CancellationToken cancellationToken = default);
    Task<UpdateStats> ApplyTo(ISift target, CancellationToken cancellationToken = default);
}

public interface ISift : IDisposable
{
    bool RequiresRebuild { get; }
    IIndexSnapshot OpenSnapshot(SearchOptions options = default);
    IOverlaySnapshot OpenOverlaySnapshot(SearchOptions options = default);
    ActivityStats GetActivityStats();
    MaintenanceStats GetMaintenanceStats();
    StorageStats GetStorageStats();
    Task<BuildStats> Build(IDocumentSource documents, CancellationToken cancellationToken = default);
    Task<UpdateStats> Update(IDocumentSource upserts, ReadOnlySpan<DocumentKey> deletions, CancellationToken cancellationToken = default);
    Task<CompactStats> Compact(CompactOptions options, CancellationToken cancellationToken = default);
}

