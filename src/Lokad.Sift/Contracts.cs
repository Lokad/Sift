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
    MaxElapsedTime,
    RegexTimeout,
    Cancelled
}

public enum TimeoutPolicy
{
    Default,
    Throw,
    StopSearch
}

public enum RejectedUpsertPolicy
{
    FailUpdate,
    PreserveExisting
}

public enum DocumentRejectionReason
{
    MaximumDocumentBytesExceeded,
    BinaryContent,
    InvalidUtf8
}

public enum DocumentContentLifetime
{
    // The source may reuse or mutate the buffer on its next TryReadNext call.
    UntilNextRead,
    // The bytes remain immutable until the Build, Update, or Apply operation completes.
    UntilOperationCompletes
}

public readonly record struct SearchQuery
{
    public SearchQuery(string pattern)
        : this(pattern, PatternMode.Regex, CaseMode.Smart)
    {
    }

    public SearchQuery(
        string pattern,
        PatternMode patternMode,
        CaseMode caseMode)
        : this(
            pattern,
            patternMode,
            caseMode,
            wholeWord: false,
            wholeLine: false,
            multiline: false,
            dotAll: false,
            maxHits: 10_000,
            maxDocuments: int.MaxValue,
            maxBytesScanned: long.MaxValue,
            timeoutPolicy: TimeoutPolicy.StopSearch,
            maxHitsPerDocument: int.MaxValue,
            maxElapsedMilliseconds: 30_000)
    {
    }

    public SearchQuery(
        string pattern,
        PatternMode patternMode,
        CaseMode caseMode,
        bool wholeWord,
        bool wholeLine,
        bool multiline,
        bool dotAll,
        int maxHits,
        int maxDocuments,
        long maxBytesScanned,
        TimeoutPolicy timeoutPolicy,
        int maxHitsPerDocument,
        long maxElapsedMilliseconds)
    {
        Pattern = pattern;
        PatternMode = patternMode;
        CaseMode = caseMode;
        WholeWord = wholeWord;
        WholeLine = wholeLine;
        Multiline = multiline;
        DotAll = dotAll;
        MaxHits = maxHits;
        MaxDocuments = maxDocuments;
        MaxBytesScanned = maxBytesScanned;
        TimeoutPolicy = timeoutPolicy;
        MaxHitsPerDocument = maxHitsPerDocument;
        MaxElapsedMilliseconds = maxElapsedMilliseconds;
    }

    public string Pattern { get; init; }
    public PatternMode PatternMode { get; init; }
    public CaseMode CaseMode { get; init; }
    public bool WholeWord { get; init; }
    public bool WholeLine { get; init; }
    public bool Multiline { get; init; }
    public bool DotAll { get; init; }
    public int MaxHits { get; init; }
    public int MaxDocuments { get; init; }
    public long MaxBytesScanned { get; init; }
    public TimeoutPolicy TimeoutPolicy { get; init; }
    public int MaxHitsPerDocument { get; init; }
    public long MaxElapsedMilliseconds { get; init; }
}

public readonly record struct SearchOptions
{
    public SearchOptions()
        : this(segmentParallelism: 1)
    {
    }

    public SearchOptions(int segmentParallelism)
    {
        SegmentParallelism = segmentParallelism;
    }

    public int SegmentParallelism { get; init; }
}

public readonly record struct PathFilter
{
    public PathFilter()
        : this(
            pathPrefix: null,
            includeGlobs: null,
            excludeGlobs: null,
            pathRegex: null)
    {
    }

    public PathFilter(
        string? pathPrefix,
        string[]? includeGlobs,
        string[]? excludeGlobs,
        string? pathRegex)
    {
        PathPrefix = pathPrefix;
        IncludeGlobs = includeGlobs;
        ExcludeGlobs = excludeGlobs;
        PathRegex = pathRegex;
    }

    public string? PathPrefix { get; init; }
    public string[]? IncludeGlobs { get; init; }
    public string[]? ExcludeGlobs { get; init; }
    public string? PathRegex { get; init; }
}

public readonly record struct RawHit(
    ulong SegmentId,
    uint DocumentOrdinal,
    int StartByte,
    int EndByte,
    int StartLine,
    int EndLine);

public readonly record struct DocumentKey(string Path);

public readonly record struct SubmittedDocument
{
    public SubmittedDocument(
        string path,
        ReadOnlyMemory<byte> content)
        : this(path, content, lastModifiedUtc: null)
    {
    }

    public SubmittedDocument(
        string path,
        ReadOnlyMemory<byte> content,
        DateTimeOffset? lastModifiedUtc)
        : this(
            path,
            content,
            lastModifiedUtc,
            DocumentContentLifetime.UntilNextRead)
    {
    }

    public SubmittedDocument(
        string path,
        ReadOnlyMemory<byte> content,
        DateTimeOffset? lastModifiedUtc,
        DocumentContentLifetime contentLifetime)
    {
        Path = path;
        Content = content;
        LastModifiedUtc = lastModifiedUtc;
        ContentLifetime = contentLifetime;
    }

    public string Path { get; init; }
    public ReadOnlyMemory<byte> Content { get; init; }
    public DateTimeOffset? LastModifiedUtc { get; init; }
    public DocumentContentLifetime ContentLifetime { get; init; }
}

public sealed class SiftOptions
{
    public int MaxDocumentBytes { get; init; } = 32 * 1024 * 1024;
    public int TargetSegmentDocumentCount { get; init; } = 50_000;
    public long TargetSegmentContentBytes { get; init; } = 64L * 1024 * 1024;
    // Caps new-file admission against the total corpus-root footprint.
    public long MaximumPhysicalBytes { get; init; } = long.MaxValue;
    // Keeps enough room for one default 1 GiB compaction pass plus its 512 MiB reserve.
    public long MinimumFreeBytesAfterMutation { get; init; } = 1536L * 1024 * 1024;
    public int DirectScanScopeDocumentsThreshold { get; init; } = 256;
    public long DirectScanScopeBytesThreshold { get; init; } = 1L * 1024 * 1024;
    public double DirectScanCandidateDensityThreshold { get; init; } = 0.85;
    public int MaxSelectedNgrams { get; init; } = 24;
    public int MaxMaterializedMatchBytes { get; init; } = 64 * 1024;
    public int MaxMaterializedContextBytes { get; init; } = 256 * 1024;
    public int MaxMaterializedContextLines { get; init; } = 1000;
    // Rejects pathological parser and compiler inputs before allocating regex state.
    public int MaxPatternCharacters { get; init; } = 2 * 1024;
    // Prevents recursive regex analysis from exhausting the process stack.
    public int MaxPatternNesting { get; init; } = 64;
    // Bounds aggregate include/exclude matcher construction per search.
    public int MaxPathFilterPatterns { get; init; } = 256;
    // Bounds aggregate prefix, glob, and path-regex preparation per search.
    public int MaxPathFilterCharacters { get; init; } = 16 * 1024;
    // Prevents consumers from checkpointing an event whose replacement was not indexed.
    public RejectedUpsertPolicy RejectedUpsertPolicy { get; init; } = RejectedUpsertPolicy.FailUpdate;
}

public sealed class RejectedUpsertException : Exception
{
    public RejectedUpsertException(
        int rejectedUpserts,
        long rejectedBytes,
        IReadOnlyList<RejectedDocument> rejectedDocuments)
        : base(CreateMessage(rejectedUpserts, rejectedBytes, rejectedDocuments))
    {
        RejectedUpserts = rejectedUpserts;
        RejectedBytes = rejectedBytes;
        RejectedDocuments = rejectedDocuments.ToArray();
    }

    public int RejectedUpserts { get; }
    public long RejectedBytes { get; }
    public IReadOnlyList<RejectedDocument> RejectedDocuments { get; }

    private static string CreateMessage(
        int rejectedUpserts,
        long rejectedBytes,
        IReadOnlyList<RejectedDocument> rejectedDocuments)
    {
        if (rejectedDocuments.Count == 0)
        {
            return $"Sift rejected {rejectedUpserts} upsert(s) containing {rejectedBytes} byte(s); the update was not committed.";
        }

        var first = rejectedDocuments[0];
        return $"Sift rejected upsert '{first.Path}' ({first.Reason}, {first.SubmittedBytes} byte(s)); the update was not committed.";
    }
}

public readonly record struct RejectedDocument(
    string Path,
    DocumentRejectionReason Reason,
    int SubmittedBytes);

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
    // Valid replacements omitted because both indexed metadata and body bytes already match.
    int SkippedUpserts,
    int RejectedUpserts,
    // Explicit deletions that removed a live document; missing paths are not counted.
    int DeletedDocuments,
    int PublishedSegments,
    long SubmittedBytes,
    long ContentBytes,
    // Normalized body bytes proven identical to their current indexed versions.
    long SkippedBytes,
    long RejectedBytes,
    long ElapsedMilliseconds);

public sealed record CompactOptions
{
    public CompactOptions()
        : this(
            maxMergedSegments: 16,
            deadFractionThreshold: 0.20,
            deadContentFractionThreshold: 0.20,
            smallSegmentMergeThreshold: 16,
            metadataBloatDeadOrdinalsThreshold: 1024,
            maxInputSegmentBytes: 1L * 1024 * 1024 * 1024,
            minimumFreeBytesAfterCompaction: 512L * 1024 * 1024)
    {
    }

    public CompactOptions(
        int maxMergedSegments,
        double deadFractionThreshold,
        double deadContentFractionThreshold,
        int smallSegmentMergeThreshold,
        int metadataBloatDeadOrdinalsThreshold,
        long maxInputSegmentBytes,
        long minimumFreeBytesAfterCompaction)
    {
        MaxMergedSegments = maxMergedSegments;
        DeadFractionThreshold = deadFractionThreshold;
        DeadContentFractionThreshold = deadContentFractionThreshold;
        SmallSegmentMergeThreshold = smallSegmentMergeThreshold;
        MetadataBloatDeadOrdinalsThreshold = metadataBloatDeadOrdinalsThreshold;
        MaxInputSegmentBytes = maxInputSegmentBytes;
        MinimumFreeBytesAfterCompaction = minimumFreeBytesAfterCompaction;
    }

    public int MaxMergedSegments { get; init; }
    // Selects segments with enough dead document rows to justify rewriting.
    public double DeadFractionThreshold { get; init; }
    // Selects segments whose deleted bodies account for this share of content bytes.
    public double DeadContentFractionThreshold { get; init; }
    // Delays clean small-segment merging until the corpus exceeds this pack count.
    public int SmallSegmentMergeThreshold { get; init; }
    public int MetadataBloatDeadOrdinalsThreshold { get; init; }
    // Applies to physical packs; one larger debt-bearing segment remains reclaimable.
    public long MaxInputSegmentBytes { get; init; }
    // Reserve left after budgeting a same-size replacement for the selected packs.
    public long MinimumFreeBytesAfterCompaction { get; init; }
}

public readonly record struct CompactStats(
    int InputSegments,
    int OutputSegments,
    int MergedDocuments,
    long ContentBytes,
    long ElapsedMilliseconds,
    long PeakManagedBytes,
    // True when the same policy can make progress in another bounded pass.
    bool HasMoreWork);

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

public enum IndexValidationState
{
    NotStarted,
    Running,
    Ready,
    Failed,
    Cancelled
}

public readonly record struct IndexValidationStats(
    // Lifecycle state of the latest explicit integrity-validation attempt.
    IndexValidationState State,
    // Manifest generation being validated, or null before a manifest is opened.
    ulong? Generation,
    // Immutable packs referenced by that generation.
    int TotalSegments,
    // Packs whose full checksums have been verified in this attempt.
    int ValidatedSegments,
    // Physical bytes across all packs in the generation.
    long TotalBytes,
    // Physical bytes whose packs have completed validation.
    long ValidatedBytes,
    // Wall-clock time spent by the current or latest attempt.
    long ElapsedMilliseconds,
    // Latest exceptional failure message, or null outside Failed state.
    string? Failure)
{
    public bool IsReady => State == IndexValidationState.Ready;
}

public readonly record struct MaintenanceStats(
    // Manifest generation observed by this snapshot.
    ulong Generation,
    // Packs referenced by the manifest.
    int SegmentCount,
    // All rows, including superseded and deleted versions.
    int TotalDocuments,
    // Rows visible to new snapshots.
    int LiveDocuments,
    // Rows retained only until compaction.
    int DeadDocuments,
    // Dead rows as a share of all rows.
    double DeadDocumentFraction,
    // Dead body bytes as a share of all body bytes.
    double DeadContentFraction,
    // Body bytes before dead content is reclaimed.
    long ContentBytes,
    // Body bytes expected to remain after compaction.
    long EstimatedLiveContentBytes,
    // Body bytes attributable to dead rows.
    long EstimatedReclaimableContentBytes,
    // Manifest entries used to mask dead rows.
    long DeadOrdinalEntries,
    // Packs containing at least one dead row.
    int SegmentsWithDeletions,
    // Live packs below both targets; diagnostic only, because they may not fit together.
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
    // Complete corpus-root footprint used for quota admission, including lifecycle metadata.
    long CorpusPhysicalBytes,
    // Configured corpus-root quota; long.MaxValue means only filesystem capacity is relevant.
    long MaximumPhysicalBytes,
    // Bytes currently admissible under both the corpus quota and filesystem capacity, when known.
    long? AvailableBytes,
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
    bool IsReclamationBlocked,
    // Number of reclamation sweeps that failed before producing structured sweep statistics.
    long ReclamationFailureCount,
    // Most recent exceptional reclamation failure, or null when no sweep has failed.
    string? LastReclamationFailure);

public readonly record struct MaterializedHit(
    string Path,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string MatchText,
    string[] BeforeContext,
    string[] AfterContext,
    bool IsMatchTruncated,
    bool IsContextTruncated);

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
    SearchStats Search(in SearchQuery query, in PathFilter pathFilter, IHitCollector collector);
    SearchStats Search(in SearchQuery query, in PathFilter pathFilter, IHitCollector collector, CancellationToken cancellationToken);
    MaterializedHit Materialize(in RawHit hit, int contextBefore, int contextAfter);
    bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content);
}

public interface IOverlaySnapshot : IIndexSnapshot
{
    Task<UpdateStats> Apply(IDocumentSource upserts, ReadOnlySpan<DocumentKey> deletions);
    Task<UpdateStats> Apply(IDocumentSource upserts, ReadOnlySpan<DocumentKey> deletions, CancellationToken cancellationToken);
    Task<UpdateStats> ApplyTo(ISift target);
    Task<UpdateStats> ApplyTo(ISift target, CancellationToken cancellationToken);
}

public interface ISift : IDisposable
{
    bool RequiresRebuild { get; }
    IIndexSnapshot OpenSnapshot();
    IIndexSnapshot OpenSnapshot(SearchOptions options);
    IOverlaySnapshot OpenOverlaySnapshot();
    IOverlaySnapshot OpenOverlaySnapshot(SearchOptions options);
    ActivityStats GetActivityStats();
    IndexValidationStats GetValidationStats();
    MaintenanceStats GetMaintenanceStats();
    StorageStats GetStorageStats();
    // Reports whether the current generation contains work selectable by this policy.
    bool ShouldCompact(CompactOptions options);
    Task<IndexValidationStats> Validate();
    Task<IndexValidationStats> Validate(CancellationToken cancellationToken);
    Task<BuildStats> Build(IDocumentSource documents);
    Task<BuildStats> Build(IDocumentSource documents, CancellationToken cancellationToken);
    Task<UpdateStats> Update(IDocumentSource upserts, ReadOnlySpan<DocumentKey> deletions);
    Task<UpdateStats> Update(IDocumentSource upserts, ReadOnlySpan<DocumentKey> deletions, CancellationToken cancellationToken);
    Task<CompactStats> Compact(CompactOptions options);
    Task<CompactStats> Compact(CompactOptions options, CancellationToken cancellationToken);
}

