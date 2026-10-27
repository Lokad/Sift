namespace Lokad.Sift;

public sealed class SiftOptions
{
    /// <summary>Gets the borrowed UTF-8 regex engine, or null for .NET verification.</summary>
    public ISiftRegexEngine? RegexEngine { get; init; }

    public const int MaximumSelectedNgramCount = 256;

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
    // Bounds opaque consumer state committed atomically with a manifest.
    public int MaxCommitMetadataBytes { get; init; } = 64 * 1024;
    // Compacts an overlay once it retains this many segments beyond the minimum
    // needed for its current live document and byte counts.
    public int OverlayCompactionSegmentThreshold { get; init; } = 8;
    // Prevents one search from creating an excessive number of segment workers.
    public int MaxSegmentParallelism { get; init; } = 64;
    // Retains the native indexing workspace between mutations for latency-sensitive writers.
    public bool RetainIndexingWorkspace { get; init; }
}

public sealed record BuildOptions
{
    // Invoked after ingestion succeeds and before the manifest is published.
    // The returned bytes are copied into the new manifest. The callback must not
    // dispose or synchronously start another mutation on the consuming index.
    // GetStorageStats and ShouldCompact on that index are also rejected here.
    // Caller exceptions propagate unchanged and do not themselves mark a healthy
    // index as corrupt; the previous committed state remains available.
    public Func<BuildCommitStats, ReadOnlyMemory<byte>>? CommitMetadataProvider { get; init; }
}

public sealed record UpdateOptions
{
    public UpdateOptions()
        : this(RejectedUpsertPolicy.FailUpdate)
    {
    }

    public UpdateOptions(RejectedUpsertPolicy rejectedUpsertPolicy)
    {
        RejectedUpsertPolicy = rejectedUpsertPolicy;
    }

    public RejectedUpsertPolicy RejectedUpsertPolicy { get; init; }
    // When present, the update is rejected unless this exact manifest identity is still current.
    // Unlike ExpectedRevision, compaction and metadata-only commits invalidate the expectation.
    public IndexIdentity? ExpectedIdentity { get; init; }
    // When present, the update is rejected unless this logical corpus revision is still current.
    // Physical generation changes caused by compaction do not invalidate the expectation.
    public IndexRevision? ExpectedRevision { get; init; }
    // Invoked after ingestion succeeds and before the manifest is published.
    // The returned bytes are copied into the new manifest. The callback must not
    // dispose or synchronously start another mutation on the consuming index.
    // GetStorageStats and ShouldCompact on that index are also rejected here.
    // Caller exceptions propagate unchanged and do not themselves mark a healthy
    // index as corrupt; the previous committed state remains available.
    public Func<UpdateCommitStats, ReadOnlyMemory<byte>>? CommitMetadataProvider { get; init; }
}

public sealed record OverlayUpdateOptions
{
    public OverlayUpdateOptions()
        : this(RejectedUpsertPolicy.FailUpdate)
    {
    }

    public OverlayUpdateOptions(RejectedUpsertPolicy rejectedUpsertPolicy)
    {
        RejectedUpsertPolicy = rejectedUpsertPolicy;
    }

    public RejectedUpsertPolicy RejectedUpsertPolicy { get; init; }
}

public sealed class StaleIndexIdentityException : Exception
{
    public StaleIndexIdentityException(IndexIdentity expectedIdentity, IndexIdentity actualIdentity)
        : base("The Sift index changed after the caller read its identity.")
    {
        ExpectedIdentity = expectedIdentity;
        ActualIdentity = actualIdentity;
    }

    public IndexIdentity ExpectedIdentity { get; }
    public IndexIdentity ActualIdentity { get; }
}

public sealed class StaleIndexRevisionException : Exception
{
    public StaleIndexRevisionException(IndexRevision expectedRevision, IndexRevision actualRevision)
        : base("The Sift index changed after the caller read its revision.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public IndexRevision ExpectedRevision { get; }
    public IndexRevision ActualRevision { get; }
}

public class BuildCommitStats
{
    private readonly RejectedDocument[] _rejectedDocumentDetails;

    internal BuildCommitStats(
        int submittedDocuments,
        int indexedDocuments,
        int rejectedDocuments,
        int segmentCount,
        long submittedBytes,
        long contentBytes,
        long rejectedBytes,
        RejectedDocument[] rejectedDocumentDetails)
    {
        SubmittedDocuments = submittedDocuments;
        IndexedDocuments = indexedDocuments;
        RejectedDocuments = rejectedDocuments;
        SegmentCount = segmentCount;
        SubmittedBytes = submittedBytes;
        ContentBytes = contentBytes;
        RejectedBytes = rejectedBytes;
        _rejectedDocumentDetails = rejectedDocumentDetails;
        RejectedDocumentDetails = PublicResultCollection.Own(rejectedDocumentDetails);
    }

    internal BuildCommitStats(BuildCommitStats stats)
        : this(
            stats.SubmittedDocuments,
            stats.IndexedDocuments,
            stats.RejectedDocuments,
            stats.SegmentCount,
            stats.SubmittedBytes,
            stats.ContentBytes,
            stats.RejectedBytes,
            stats._rejectedDocumentDetails)
    {
    }

    public int SubmittedDocuments { get; }
    public int IndexedDocuments { get; }
    public int RejectedDocuments { get; }
    public int SegmentCount { get; }
    public long SubmittedBytes { get; }
    public long ContentBytes { get; }
    public long RejectedBytes { get; }
    /// <summary>Documents omitted from the pending build, with their rejection reasons.</summary>
    public IReadOnlyList<RejectedDocument> RejectedDocumentDetails { get; }
}

public sealed class BuildStats : BuildCommitStats
{
    internal BuildStats(
        BuildCommitStats pending,
        long elapsedMilliseconds,
        IndexIdentity committedIdentity)
        : base(pending)
    {
        ElapsedMilliseconds = elapsedMilliseconds;
        CommittedIdentity = committedIdentity;
    }

    public long ElapsedMilliseconds { get; }

    /// <summary>Identity of the index state committed by the completed build.</summary>
    public IndexIdentity CommittedIdentity { get; }
}

public class UpdateCommitStats
{
    private readonly RejectedDocument[] _rejectedDocumentDetails;

    internal UpdateCommitStats(
        int submittedUpserts,
        int upsertedDocuments,
        int skippedUpserts,
        int rejectedUpserts,
        int deletedDocuments,
        int publishedSegments,
        long submittedBytes,
        long contentBytes,
        long skippedBytes,
        long rejectedBytes,
        RejectedDocument[] rejectedDocumentDetails)
    {
        SubmittedUpserts = submittedUpserts;
        UpsertedDocuments = upsertedDocuments;
        SkippedUpserts = skippedUpserts;
        RejectedUpserts = rejectedUpserts;
        DeletedDocuments = deletedDocuments;
        PublishedSegments = publishedSegments;
        SubmittedBytes = submittedBytes;
        ContentBytes = contentBytes;
        SkippedBytes = skippedBytes;
        RejectedBytes = rejectedBytes;
        _rejectedDocumentDetails = rejectedDocumentDetails;
        RejectedDocumentDetails = PublicResultCollection.Own(rejectedDocumentDetails);
    }

    internal UpdateCommitStats(UpdateCommitStats stats)
        : this(
            stats.SubmittedUpserts,
            stats.UpsertedDocuments,
            stats.SkippedUpserts,
            stats.RejectedUpserts,
            stats.DeletedDocuments,
            stats.PublishedSegments,
            stats.SubmittedBytes,
            stats.ContentBytes,
            stats.SkippedBytes,
            stats.RejectedBytes,
            stats._rejectedDocumentDetails)
    {
    }

    public int SubmittedUpserts { get; }
    public int UpsertedDocuments { get; }
    // Valid replacements omitted because both indexed metadata and body bytes already match.
    public int SkippedUpserts { get; }
    public int RejectedUpserts { get; }
    // Explicit deletions that removed a live document; missing paths are not counted.
    public int DeletedDocuments { get; }
    public int PublishedSegments { get; }
    public long SubmittedBytes { get; }
    public long ContentBytes { get; }
    // Normalized body bytes proven identical to their current indexed versions.
    public long SkippedBytes { get; }
    public long RejectedBytes { get; }
    public IReadOnlyList<RejectedDocument> RejectedDocumentDetails { get; }
}

public sealed class UpdateStats : UpdateCommitStats
{
    internal UpdateStats(
        UpdateCommitStats pending,
        long elapsedMilliseconds,
        IndexIdentity committedIdentity)
        : base(pending)
    {
        ElapsedMilliseconds = elapsedMilliseconds;
        CommittedIdentity = committedIdentity;
    }

    public long ElapsedMilliseconds { get; }

    /// <summary>Identity of the index state returned by the completed update.</summary>
    public IndexIdentity CommittedIdentity { get; }
}

public readonly record struct OverlayRevision(ulong Value);

public sealed class OverlayUpdateStats : UpdateCommitStats
{
    internal OverlayUpdateStats(
        UpdateCommitStats pending,
        long elapsedMilliseconds,
        OverlayRevision committedRevision)
        : base(pending)
    {
        ElapsedMilliseconds = elapsedMilliseconds;
        CommittedRevision = committedRevision;
    }

    public long ElapsedMilliseconds { get; }
    public OverlayRevision CommittedRevision { get; }
}

public sealed record CompactOptions
{
    public CompactOptions()
        : this(
            maxMergedSegments: 16,
            deadFractionThreshold: 0.20,
            deadContentFractionThreshold: 0.20,
            minimumSegmentCountForSmallMerge: 16,
            metadataBloatDeadOrdinalsThreshold: 1024,
            maxInputSegmentBytes: 1L * 1024 * 1024 * 1024,
            minimumFreeBytesAfterCompaction: 512L * 1024 * 1024)
    {
    }

    private CompactOptions(
        int maxMergedSegments,
        double deadFractionThreshold,
        double deadContentFractionThreshold,
        int minimumSegmentCountForSmallMerge,
        int metadataBloatDeadOrdinalsThreshold,
        long maxInputSegmentBytes,
        long minimumFreeBytesAfterCompaction)
    {
        MaxMergedSegments = maxMergedSegments;
        DeadFractionThreshold = deadFractionThreshold;
        DeadContentFractionThreshold = deadContentFractionThreshold;
        MinimumSegmentCountForSmallMerge = minimumSegmentCountForSmallMerge;
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
    public int MinimumSegmentCountForSmallMerge { get; init; }
    public int MetadataBloatDeadOrdinalsThreshold { get; init; }
    // Applies to physical packs; one larger debt-bearing segment remains reclaimable.
    public long MaxInputSegmentBytes { get; init; }
    // Reserve left after budgeting a same-size replacement for the selected packs.
    public long MinimumFreeBytesAfterCompaction { get; init; }
}

public sealed class CompactStats
{
    internal CompactStats(
        int inputSegments,
        int outputSegments,
        int mergedDocuments,
        long contentBytes,
        long elapsedMilliseconds,
        bool hasMoreWork)
    {
        InputSegments = inputSegments;
        OutputSegments = outputSegments;
        MergedDocuments = mergedDocuments;
        ContentBytes = contentBytes;
        ElapsedMilliseconds = elapsedMilliseconds;
        HasMoreWork = hasMoreWork;
    }

    public int InputSegments { get; }
    public int OutputSegments { get; }
    public int MergedDocuments { get; }
    public long ContentBytes { get; }
    public long ElapsedMilliseconds { get; }
    // True when the same policy can make progress in another bounded pass.
    public bool HasMoreWork { get; }
}

public enum SiftWriteActivity
{
    Idle,
    Build,
    Update,
    Compact
}

public readonly record struct WriteActivityStats
{
    internal WriteActivityStats(bool isWriteBusy, SiftWriteActivity currentActivity)
    {
        IsWriteBusy = isWriteBusy;
        CurrentActivity = currentActivity;
    }

    // True while a mutation is running or waiting for the writer gate.
    public bool IsWriteBusy { get; }
    // Idle while work is queued and no mutation body has started.
    public SiftWriteActivity CurrentActivity { get; }
}

public readonly record struct IndexRevision(
    // Changes whenever a full build replaces the logical corpus.
    CorpusId CorpusId,
    // Changes only when the visible document set or content changes.
    ContentRevision ContentRevision);

public readonly record struct IndexIdentity(
    CorpusId CorpusId,
    ContentRevision ContentRevision,
    // Changes whenever a manifest is published, including compaction and metadata-only commits.
    StorageGeneration StorageGeneration)
{
    public IndexRevision Revision => new(CorpusId, ContentRevision);
}

public sealed class IndexState
{
    internal IndexState(IndexIdentity identity, ReadOnlyMemory<byte> commitMetadata)
    {
        Identity = identity;
        CommitMetadata = commitMetadata;
    }

    public IndexIdentity Identity { get; }
    public ReadOnlyMemory<byte> CommitMetadata { get; }
}

public enum IndexValidationState
{
    NotStarted,
    Running,
    Ready,
    Failed,
    Cancelled
}

public sealed class IndexValidationStats
{
    internal IndexValidationStats(
        IndexValidationState state,
        StorageGeneration? generation,
        int totalSegments,
        int validatedSegments,
        long totalBytes,
        long validatedBytes,
        long elapsedMilliseconds,
        string? failure)
    {
        State = state;
        Generation = generation;
        TotalSegments = totalSegments;
        ValidatedSegments = validatedSegments;
        TotalBytes = totalBytes;
        ValidatedBytes = validatedBytes;
        ElapsedMilliseconds = elapsedMilliseconds;
        Failure = failure;
    }

    // Lifecycle state of the latest explicit integrity-validation attempt.
    public IndexValidationState State { get; }
    // Manifest generation being validated, or null before a manifest is opened.
    public StorageGeneration? Generation { get; }
    // Immutable packs referenced by that generation.
    public int TotalSegments { get; }
    // Packs whose checksums and persisted structures have been verified in this attempt.
    public int ValidatedSegments { get; }
    // Physical bytes across all packs in the generation.
    public long TotalBytes { get; }
    // Physical bytes whose packs have completed validation.
    public long ValidatedBytes { get; }
    // Wall-clock time spent by the current or latest attempt.
    public long ElapsedMilliseconds { get; }
    // Latest exceptional failure message, or null outside Failed state.
    public string? Failure { get; }

    public bool IsReady => State == IndexValidationState.Ready;

    internal IndexValidationStats WithState(IndexValidationState state, string? failure) => new(
        state,
        Generation,
        TotalSegments,
        ValidatedSegments,
        TotalBytes,
        ValidatedBytes,
        ElapsedMilliseconds,
        failure);
}

public sealed class MaintenanceStats
{
    internal MaintenanceStats(
        StorageGeneration generation,
        int segmentCount,
        int totalDocuments,
        int liveDocuments,
        int deadDocuments,
        double deadDocumentFraction,
        double deadContentFraction,
        long contentBytes,
        long estimatedLiveContentBytes,
        long estimatedReclaimableContentBytes,
        long deadOrdinalEntries,
        int segmentsWithDeletions,
        int smallSegmentCount)
    {
        Generation = generation;
        SegmentCount = segmentCount;
        TotalDocuments = totalDocuments;
        LiveDocuments = liveDocuments;
        DeadDocuments = deadDocuments;
        DeadDocumentFraction = deadDocumentFraction;
        DeadContentFraction = deadContentFraction;
        ContentBytes = contentBytes;
        EstimatedLiveContentBytes = estimatedLiveContentBytes;
        EstimatedReclaimableContentBytes = estimatedReclaimableContentBytes;
        DeadOrdinalEntries = deadOrdinalEntries;
        SegmentsWithDeletions = segmentsWithDeletions;
        SmallSegmentCount = smallSegmentCount;
    }

    // Manifest generation observed by this snapshot.
    public StorageGeneration Generation { get; }
    // Packs referenced by the manifest.
    public int SegmentCount { get; }
    // All rows, including superseded and deleted versions.
    public int TotalDocuments { get; }
    // Rows visible to new snapshots.
    public int LiveDocuments { get; }
    // Rows retained only until compaction.
    public int DeadDocuments { get; }
    // Dead rows as a share of all rows.
    public double DeadDocumentFraction { get; }
    // Dead body bytes as a share of all body bytes.
    public double DeadContentFraction { get; }
    // Body bytes before dead content is reclaimed.
    public long ContentBytes { get; }
    // Body bytes expected to remain after compaction.
    public long EstimatedLiveContentBytes { get; }
    // Body bytes attributable to dead rows.
    public long EstimatedReclaimableContentBytes { get; }
    // Manifest entries used to mask dead rows.
    public long DeadOrdinalEntries { get; }
    // Packs containing at least one dead row.
    public int SegmentsWithDeletions { get; }
    // Live packs below both targets; diagnostic only, because they may not fit together.
    public int SmallSegmentCount { get; }
}

public sealed class StorageStats
{
    internal StorageStats(
        int manifestSegmentCount,
        long manifestSegmentBytes,
        int physicalSegmentCount,
        long physicalSegmentBytes,
        int retiredSegmentCount,
        long retiredSegmentBytes,
        long temporaryBytes,
        long corpusPhysicalBytes,
        long maximumPhysicalBytes,
        long? availableBytes,
        int leasedGenerationCount,
        StorageGeneration? oldestLeasedGeneration,
        int snapshotDeferredSegmentCount,
        long snapshotDeferredSegmentBytes,
        int pendingDeletionSegmentCount,
        long pendingDeletionSegmentBytes,
        int blockedReclamationSegmentCount,
        long blockedReclamationSegmentBytes,
        bool isReclamationBlocked,
        long reclamationFailureCount,
        string? lastReclamationFailure)
    {
        ManifestSegmentCount = manifestSegmentCount;
        ManifestSegmentBytes = manifestSegmentBytes;
        PhysicalSegmentCount = physicalSegmentCount;
        PhysicalSegmentBytes = physicalSegmentBytes;
        RetiredSegmentCount = retiredSegmentCount;
        RetiredSegmentBytes = retiredSegmentBytes;
        TemporaryBytes = temporaryBytes;
        CorpusPhysicalBytes = corpusPhysicalBytes;
        MaximumPhysicalBytes = maximumPhysicalBytes;
        AvailableBytes = availableBytes;
        LeasedGenerationCount = leasedGenerationCount;
        OldestLeasedGeneration = oldestLeasedGeneration;
        SnapshotDeferredSegmentCount = snapshotDeferredSegmentCount;
        SnapshotDeferredSegmentBytes = snapshotDeferredSegmentBytes;
        PendingDeletionSegmentCount = pendingDeletionSegmentCount;
        PendingDeletionSegmentBytes = pendingDeletionSegmentBytes;
        BlockedReclamationSegmentCount = blockedReclamationSegmentCount;
        BlockedReclamationSegmentBytes = blockedReclamationSegmentBytes;
        IsReclamationBlocked = isReclamationBlocked;
        ReclamationFailureCount = reclamationFailureCount;
        LastReclamationFailure = lastReclamationFailure;
    }

    // Currently reachable packs that are also present on disk.
    public int ManifestSegmentCount { get; }
    // On-disk lengths of those packs; this excludes manifest metadata.
    public long ManifestSegmentBytes { get; }
    // All well-named directories containing a pack, including unreachable packs.
    public int PhysicalSegmentCount { get; }
    // Segment-pack footprint only; temporary and lifecycle files are excluded.
    public long PhysicalSegmentBytes { get; }
    // Packs unreachable from the current manifest, regardless of why they remain.
    public int RetiredSegmentCount { get; }
    // On-disk lengths of the unreachable packs.
    public long RetiredSegmentBytes { get; }
    // Incomplete publication files, not retired packs awaiting reclamation.
    public long TemporaryBytes { get; }
    // Complete corpus-root footprint used for quota admission, including lifecycle metadata.
    public long CorpusPhysicalBytes { get; }
    // Configured corpus-root quota; long.MaxValue means only filesystem capacity is relevant.
    public long MaximumPhysicalBytes { get; }
    // Bytes currently admissible under both the corpus quota and filesystem capacity, when known.
    public long? AvailableBytes { get; }
    // Non-current generations held open by at least one reader.
    public int LeasedGenerationCount { get; }
    // Lowest held generation, or null when no superseded snapshot remains.
    public StorageGeneration? OldestLeasedGeneration { get; }
    // Retired packs retained solely because a live snapshot can still read them.
    public int SnapshotDeferredSegmentCount { get; }
    // On-disk lengths of the snapshot-protected packs.
    public long SnapshotDeferredSegmentBytes { get; }
    // Retired packs proven unreachable but not removed by the latest sweep.
    public int PendingDeletionSegmentCount { get; }
    // On-disk lengths still eligible for deletion.
    public long PendingDeletionSegmentBytes { get; }
    // Retired packs whose reachability cannot be proven from lifecycle metadata.
    public int BlockedReclamationSegmentCount { get; }
    // On-disk lengths withheld from deletion because reachability is uncertain.
    public long BlockedReclamationSegmentBytes { get; }
    // True when metadata uncertainty prevents destructive reclamation.
    public bool IsReclamationBlocked { get; }
    // Number of reclamation sweeps that failed before producing structured sweep statistics.
    public long ReclamationFailureCount { get; }
    // Most recent exceptional reclamation failure, or null when no sweep has failed.
    public string? LastReclamationFailure { get; }
}

public interface IIndexSnapshot : IDisposable
{
    /// <summary>Gets the persisted index identity captured when this snapshot was opened.</summary>
    /// <remarks>For an overlay, this identifies its immutable base; overlay updates do not change it.</remarks>
    IndexIdentity Identity { get; }
    /// <summary>Gets the opaque commit metadata captured with this snapshot's manifest.</summary>
    ReadOnlyMemory<byte> CommitMetadata { get; }

    DocumentVisitStats VisitDocuments(IDocumentKeyCollector collector, CancellationToken cancellationToken);
    /// <summary>Visits a sound superset of documents that may match the query within a content range.</summary>
    /// <remarks>
    /// No content matching is performed. The caller must verify the selected range exactly,
    /// including its anchors. Candidates may include every document in the path scope.
    /// Order is the same as an unlimited empty-query Search with this filter, omitting
    /// non-candidates. Particular false positives and selected index plans may change.
    /// Query matching flags guide planning; query hit/byte/time limits and timeout policy
    /// are replaced by the visit options. Regex preparation errors, including timeouts, throw.
    /// The default implementation validates the query and lists the complete path scope,
    /// preserving existing snapshot implementations. Built-in snapshots use their indexes.
    /// Custom fallback preparation uses Explain; cancellation and elapsed time are
    /// observed before and after that existing operation, rather than interrupting it.
    /// </remarks>
    CandidateVisitStats VisitCandidates(SearchQuery query, PathFilter pathFilter,
        IDocumentCandidateCollector collector, CandidateVisitOptions options, CancellationToken cancellationToken) =>
        global::Lokad.Sift.Search.CandidateVisit.Run(this, query, pathFilter, collector, options, cancellationToken, useIndex: false);
    SearchPlan Explain(SearchQuery query, PathFilter pathFilter);
    SearchStats Search(SearchQuery query, PathFilter pathFilter, IHitCollector collector, CancellationToken cancellationToken);

    /// <summary>Materializes a raw hit produced by this snapshot.</summary>
    /// <remarks>
    ///     Hits produced by an overlay remain valid until its next update. All hits
    ///     become invalid when their producing snapshot is disposed.
    /// </remarks>
    MaterializedHit Materialize(in RawHit hit, int contextBefore, int contextAfter);

    /// <summary>Gets the document key for a raw hit produced by this snapshot.</summary>
    /// <remarks>
    ///     The hit has the same lifetime and ownership requirements as <see cref="Materialize"/>.
    ///     The returned key owns its path and remains valid after the snapshot is disposed.
    ///     Built-in snapshots retrieve the key without materializing match or context text.
    ///     The default implementation preserves compatibility with other snapshot implementations.
    /// </remarks>
    DocumentKey GetDocumentKey(in RawHit hit) => new(Materialize(hit, 0, 0).Path);

    /// <summary>Materializes a raw hit and the complete line containing its start.</summary>
    /// <remarks>
    ///     The matching line is not subject to context byte limits; callers should use
    ///     ordinary <see cref="Materialize"/> when they do not need that allocation.
    /// </remarks>
    MaterializedHitWithLine MaterializeWithMatchingLine(
        in RawHit hit,
        int contextBefore,
        int contextAfter);

    /// <summary>Gets a document body owned by this snapshot.</summary>
    /// <remarks>The returned memory remains valid until this snapshot is disposed.</remarks>
    bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content);

    /// <summary>Gets the complete document body for a raw hit produced by this snapshot.</summary>
    /// <remarks>
    ///     The hit has the same lifetime and ownership requirements as <see cref="Materialize"/>.
    ///     The returned memory is owned by the snapshot and remains valid until it is disposed,
    ///     including across overlay updates. Built-in snapshots resolve the hit directly;
    ///     the default implementation uses key lookup for existing custom implementations.
    /// </remarks>
    ReadOnlyMemory<byte> GetDocumentBytes(in RawHit hit)
    {
        if (TryGetDocumentBytes(GetDocumentKey(hit), out var content))
            return content;
        throw new InvalidOperationException("The hit's document is not available in this snapshot.");
    }
}

public interface IOverlaySnapshot : IIndexSnapshot
{
    /// <remarks><paramref name="deletions"/> must remain unchanged until the returned task completes.</remarks>
    Task<OverlayUpdateStats> Update(IDocumentSource upserts, IReadOnlyList<DocumentKey> deletions, CancellationToken cancellationToken);

    /// <remarks><paramref name="deletions"/> must remain unchanged until the returned task completes.</remarks>
    Task<OverlayUpdateStats> Update(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayUpdateOptions options,
        CancellationToken cancellationToken);
    /// <summary>Applies this overlay to its captured base if that logical revision is still current.</summary>
    /// <remarks>
    /// Any supplied expected revision must identify the same captured base revision.
    /// Calls from this overlay's active source, metadata or collector callbacks are
    /// rejected with <see cref="InvalidOperationException"/>, including inherited Task.Run contexts.
    /// </remarks>
    Task<UpdateStats> CommitToBase(ISiftIndex baseIndex, UpdateOptions options, CancellationToken cancellationToken);

    /// <summary>Applies this overlay's upserts and deletions to an arbitrary target.</summary>
    /// <remarks>
    /// The supplied update options, including any expected revision, are forwarded unchanged.
    /// Calls from this overlay's active source, metadata or collector callbacks are
    /// rejected with <see cref="InvalidOperationException"/>, including inherited Task.Run contexts.
    /// </remarks>
    Task<UpdateStats> ApplyDeltaTo(ISiftIndex target, UpdateOptions options, CancellationToken cancellationToken);
}

public interface ISiftIndex : IDisposable
{
    /// <summary>Gets whether discovered storage corruption requires a full replacement build.</summary>
    /// <remarks>Online operations defer full pack checksums, so false is not an integrity proof.</remarks>
    bool RequiresRebuild { get; }
    IndexIdentity GetIndexIdentity();
    IndexState GetIndexState();
    IIndexSnapshot OpenSnapshot(SearchOptions options);
    IOverlaySnapshot OpenOverlaySnapshot(SearchOptions options);
    WriteActivityStats GetWriteActivityStats();
    IndexValidationStats GetValidationStats();
    MaintenanceStats GetMaintenanceStats();
    /// <summary>Gets physical storage statistics while holding the writer lock.</summary>
    /// <remarks>Other callers may wait for an active writer. Callback-safe reads include GetMaintenanceStats and GetIndexState.</remarks>
    /// <exception cref="InvalidOperationException">Called from one of this index's active mutation callbacks.</exception>
    StorageStats GetStorageStats();
    /// <summary>Reports whether the current generation contains work selectable by this policy.</summary>
    /// <remarks>Acquires the writer lock; other callers may wait for an active writer.</remarks>
    /// <exception cref="InvalidOperationException">Called from one of this index's active mutation callbacks.</exception>
    bool ShouldCompact(CompactOptions options);
    /// <summary>Checks every referenced pack's checksums and persisted structures.</summary>
    Task<IndexValidationStats> Validate(CancellationToken cancellationToken);
    Task<BuildStats> Build(
        IDocumentSource documents,
        BuildOptions options,
        CancellationToken cancellationToken);
    Task<BuildStats> Build(
        IAsyncEnumerable<SubmittedDocument> documents,
        BuildOptions options,
        CancellationToken cancellationToken);

    /// <remarks><paramref name="deletions"/> must remain unchanged until the returned task completes.</remarks>
    Task<UpdateStats> Update(IDocumentSource upserts, IReadOnlyList<DocumentKey> deletions, CancellationToken cancellationToken);

    /// <remarks><paramref name="deletions"/> must remain unchanged until the returned task completes.</remarks>
    Task<UpdateStats> Update(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken);

    /// <remarks><paramref name="deletions"/> must remain unchanged until the returned task completes.</remarks>
    Task<UpdateStats> Update(
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions,
        CancellationToken cancellationToken);

    /// <remarks><paramref name="deletions"/> must remain unchanged until the returned task completes.</remarks>
    Task<UpdateStats> Update(
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken);
    Task<CompactStats> Compact(CompactOptions options, CancellationToken cancellationToken);
}
