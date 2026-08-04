namespace Lokad.Sift;

public sealed class SiftOptions
{
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
    // Enables process-wide managed-heap sampling during build and compaction.
    public bool MeasurePeakManagedMemory { get; init; }
}

public sealed record BuildOptions
{
    // Invoked after ingestion succeeds and before the manifest is published.
    // The returned bytes are copied into the new manifest.
    public Func<BuildStats, ReadOnlyMemory<byte>>? CommitMetadataProvider { get; init; }
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
    // When present, the update is rejected unless this logical corpus revision is still current.
    // Physical generation changes caused by compaction do not invalidate the expectation.
    public IndexIdentity? ExpectedIdentity { get; init; }
    // Invoked after ingestion succeeds and before the manifest is published.
    // The returned bytes are copied into the new manifest.
    public Func<UpdateStats, ReadOnlyMemory<byte>>? CommitMetadataProvider { get; init; }
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

public sealed class StaleIndexStateException : Exception
{
    public StaleIndexStateException(IndexIdentity expectedIdentity, IndexIdentity actualIdentity)
        : base("The Sift index changed after the caller read its state.")
    {
        ExpectedIdentity = expectedIdentity;
        ActualIdentity = actualIdentity;
    }

    public IndexIdentity ExpectedIdentity { get; }
    public IndexIdentity ActualIdentity { get; }
}

public sealed class BuildStats
{
    internal BuildStats(in BuildStatsData data)
    {
        SubmittedDocuments = data.SubmittedDocuments;
        IndexedDocuments = data.IndexedDocuments;
        RejectedDocuments = data.RejectedDocuments;
        SegmentCount = data.SegmentCount;
        SubmittedBytes = data.SubmittedBytes;
        ContentBytes = data.ContentBytes;
        RejectedBytes = data.RejectedBytes;
        ElapsedMilliseconds = data.ElapsedMilliseconds;
        PeakManagedBytes = data.PeakManagedBytes;
        RejectedDocumentDetails = PublicResultCollection.Freeze(data.RejectedDocumentDetails);
        CommittedIdentity = data.CommittedIdentity;
    }

    public int SubmittedDocuments { get; }
    public int IndexedDocuments { get; }
    public int RejectedDocuments { get; }
    public int SegmentCount { get; }
    public long SubmittedBytes { get; }
    public long ContentBytes { get; }
    public long RejectedBytes { get; }
    public long ElapsedMilliseconds { get; }
    // Zero when SiftOptions.MeasurePeakManagedMemory is disabled.
    public long PeakManagedBytes { get; }

    /// <summary>Documents omitted from the completed build, with their rejection reasons.</summary>
    public IReadOnlyList<RejectedDocument> RejectedDocumentDetails { get; }

    /// <summary>Identity of the index state committed by the completed build.</summary>
    /// <remarks>
    ///     The provisional statistics passed to
    ///     <see cref="BuildOptions.CommitMetadataProvider"/> have the default identity
    ///     because the manifest has not yet been committed.
    /// </remarks>
    public IndexIdentity CommittedIdentity { get; }

    internal BuildStats WithPeakManagedBytes(long peakManagedBytes) => new(new BuildStatsData
    {
        SubmittedDocuments = SubmittedDocuments,
        IndexedDocuments = IndexedDocuments,
        RejectedDocuments = RejectedDocuments,
        SegmentCount = SegmentCount,
        SubmittedBytes = SubmittedBytes,
        ContentBytes = ContentBytes,
        RejectedBytes = RejectedBytes,
        ElapsedMilliseconds = ElapsedMilliseconds,
        PeakManagedBytes = peakManagedBytes,
        RejectedDocumentDetails = RejectedDocumentDetails,
        CommittedIdentity = CommittedIdentity
    });
}

internal readonly struct BuildStatsData
{
    public required int SubmittedDocuments { get; init; }
    public required int IndexedDocuments { get; init; }
    public required int RejectedDocuments { get; init; }
    public required int SegmentCount { get; init; }
    public required long SubmittedBytes { get; init; }
    public required long ContentBytes { get; init; }
    public required long RejectedBytes { get; init; }
    public required long ElapsedMilliseconds { get; init; }
    public required long PeakManagedBytes { get; init; }
    public required IReadOnlyList<RejectedDocument> RejectedDocumentDetails { get; init; }
    public required IndexIdentity CommittedIdentity { get; init; }
}

public sealed class UpdateStats
{
    internal UpdateStats(in UpdateStatsData data)
    {
        SubmittedUpserts = data.SubmittedUpserts;
        UpsertedDocuments = data.UpsertedDocuments;
        SkippedUpserts = data.SkippedUpserts;
        RejectedUpserts = data.RejectedUpserts;
        DeletedDocuments = data.DeletedDocuments;
        PublishedSegments = data.PublishedSegments;
        SubmittedBytes = data.SubmittedBytes;
        ContentBytes = data.ContentBytes;
        SkippedBytes = data.SkippedBytes;
        RejectedBytes = data.RejectedBytes;
        RejectedDocumentDetails = PublicResultCollection.Freeze(data.RejectedDocumentDetails);
        ElapsedMilliseconds = data.ElapsedMilliseconds;
        CommittedIdentity = data.CommittedIdentity;
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
    public long ElapsedMilliseconds { get; }

    /// <summary>Identity of the index state returned by the completed update.</summary>
    /// <remarks>
    ///     This is set on the final result returned by <c>Update</c>. The provisional
    ///     statistics passed to <see cref="UpdateOptions.CommitMetadataProvider"/>
    ///     have the default identity because the manifest has not yet been committed.
    /// </remarks>
    public IndexIdentity CommittedIdentity { get; }
}

internal readonly struct UpdateStatsData
{
    public required int SubmittedUpserts { get; init; }
    public required int UpsertedDocuments { get; init; }
    public required int SkippedUpserts { get; init; }
    public required int RejectedUpserts { get; init; }
    public required int DeletedDocuments { get; init; }
    public required int PublishedSegments { get; init; }
    public required long SubmittedBytes { get; init; }
    public required long ContentBytes { get; init; }
    public required long SkippedBytes { get; init; }
    public required long RejectedBytes { get; init; }
    public required IReadOnlyList<RejectedDocument> RejectedDocumentDetails { get; init; }
    public required long ElapsedMilliseconds { get; init; }
    public required IndexIdentity CommittedIdentity { get; init; }
}

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

public sealed class CompactStats
{
    internal CompactStats(in CompactStatsData data)
    {
        InputSegments = data.InputSegments;
        OutputSegments = data.OutputSegments;
        MergedDocuments = data.MergedDocuments;
        ContentBytes = data.ContentBytes;
        ElapsedMilliseconds = data.ElapsedMilliseconds;
        PeakManagedBytes = data.PeakManagedBytes;
        HasMoreWork = data.HasMoreWork;
    }

    public int InputSegments { get; }
    public int OutputSegments { get; }
    public int MergedDocuments { get; }
    public long ContentBytes { get; }
    public long ElapsedMilliseconds { get; }
    // Zero when SiftOptions.MeasurePeakManagedMemory is disabled.
    public long PeakManagedBytes { get; }
    // True when the same policy can make progress in another bounded pass.
    public bool HasMoreWork { get; }

    internal CompactStats WithPeakManagedBytes(long peakManagedBytes) => new(new CompactStatsData
    {
        InputSegments = InputSegments,
        OutputSegments = OutputSegments,
        MergedDocuments = MergedDocuments,
        ContentBytes = ContentBytes,
        ElapsedMilliseconds = ElapsedMilliseconds,
        PeakManagedBytes = peakManagedBytes,
        HasMoreWork = HasMoreWork
    });
}

internal readonly struct CompactStatsData
{
    public required int InputSegments { get; init; }
    public required int OutputSegments { get; init; }
    public required int MergedDocuments { get; init; }
    public required long ContentBytes { get; init; }
    public required long ElapsedMilliseconds { get; init; }
    public required long PeakManagedBytes { get; init; }
    public required bool HasMoreWork { get; init; }
}

public enum SiftWriteActivity
{
    Idle,
    Build,
    Update,
    Compact
}

public readonly record struct WriteActivityStats(
    bool IsWriteBusy,
    SiftWriteActivity CurrentActivity);

public readonly record struct IndexIdentity(
    // Changes whenever a full build replaces the logical corpus.
    CorpusId CorpusId,
    // Changes only when the visible document set or content changes.
    ContentRevision ContentRevision,
    // Changes whenever a manifest is published, including compaction and metadata-only commits.
    StorageGeneration StorageGeneration);

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

public sealed class MaintenanceStats
{
    internal MaintenanceStats(in MaintenanceStatsData data)
    {
        Generation = data.Generation;
        SegmentCount = data.SegmentCount;
        TotalDocuments = data.TotalDocuments;
        LiveDocuments = data.LiveDocuments;
        DeadDocuments = data.DeadDocuments;
        DeadDocumentFraction = data.DeadDocumentFraction;
        DeadContentFraction = data.DeadContentFraction;
        ContentBytes = data.ContentBytes;
        EstimatedLiveContentBytes = data.EstimatedLiveContentBytes;
        EstimatedReclaimableContentBytes = data.EstimatedReclaimableContentBytes;
        DeadOrdinalEntries = data.DeadOrdinalEntries;
        SegmentsWithDeletions = data.SegmentsWithDeletions;
        SmallSegmentCount = data.SmallSegmentCount;
    }

    // Manifest generation observed by this snapshot.
    public ulong Generation { get; }
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

internal readonly struct MaintenanceStatsData
{
    public required ulong Generation { get; init; }
    public required int SegmentCount { get; init; }
    public required int TotalDocuments { get; init; }
    public required int LiveDocuments { get; init; }
    public required int DeadDocuments { get; init; }
    public required double DeadDocumentFraction { get; init; }
    public required double DeadContentFraction { get; init; }
    public required long ContentBytes { get; init; }
    public required long EstimatedLiveContentBytes { get; init; }
    public required long EstimatedReclaimableContentBytes { get; init; }
    public required long DeadOrdinalEntries { get; init; }
    public required int SegmentsWithDeletions { get; init; }
    public required int SmallSegmentCount { get; init; }
}

public sealed class StorageStats
{
    internal StorageStats(in StorageStatsData data)
    {
        ManifestSegmentCount = data.ManifestSegmentCount;
        ManifestSegmentBytes = data.ManifestSegmentBytes;
        PhysicalSegmentCount = data.PhysicalSegmentCount;
        PhysicalSegmentBytes = data.PhysicalSegmentBytes;
        RetiredSegmentCount = data.RetiredSegmentCount;
        RetiredSegmentBytes = data.RetiredSegmentBytes;
        TemporaryBytes = data.TemporaryBytes;
        CorpusPhysicalBytes = data.CorpusPhysicalBytes;
        MaximumPhysicalBytes = data.MaximumPhysicalBytes;
        AvailableBytes = data.AvailableBytes;
        LeasedGenerationCount = data.LeasedGenerationCount;
        OldestLeasedGeneration = data.OldestLeasedGeneration;
        SnapshotDeferredSegmentCount = data.SnapshotDeferredSegmentCount;
        SnapshotDeferredSegmentBytes = data.SnapshotDeferredSegmentBytes;
        PendingDeletionSegmentCount = data.PendingDeletionSegmentCount;
        PendingDeletionSegmentBytes = data.PendingDeletionSegmentBytes;
        BlockedReclamationSegmentCount = data.BlockedReclamationSegmentCount;
        BlockedReclamationSegmentBytes = data.BlockedReclamationSegmentBytes;
        IsReclamationBlocked = data.IsReclamationBlocked;
        ReclamationFailureCount = data.ReclamationFailureCount;
        LastReclamationFailure = data.LastReclamationFailure;
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
    public ulong? OldestLeasedGeneration { get; }
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

internal readonly struct StorageStatsData
{
    public required int ManifestSegmentCount { get; init; }
    public required long ManifestSegmentBytes { get; init; }
    public required int PhysicalSegmentCount { get; init; }
    public required long PhysicalSegmentBytes { get; init; }
    public required int RetiredSegmentCount { get; init; }
    public required long RetiredSegmentBytes { get; init; }
    public required long TemporaryBytes { get; init; }
    public required long CorpusPhysicalBytes { get; init; }
    public required long MaximumPhysicalBytes { get; init; }
    public required long? AvailableBytes { get; init; }
    public required int LeasedGenerationCount { get; init; }
    public required ulong? OldestLeasedGeneration { get; init; }
    public required int SnapshotDeferredSegmentCount { get; init; }
    public required long SnapshotDeferredSegmentBytes { get; init; }
    public required int PendingDeletionSegmentCount { get; init; }
    public required long PendingDeletionSegmentBytes { get; init; }
    public required int BlockedReclamationSegmentCount { get; init; }
    public required long BlockedReclamationSegmentBytes { get; init; }
    public required bool IsReclamationBlocked { get; init; }
    public required long ReclamationFailureCount { get; init; }
    public required string? LastReclamationFailure { get; init; }
}

public interface IIndexSnapshot : IDisposable
{
    DocumentVisitStats VisitDocuments(IDocumentKeyCollector collector, CancellationToken cancellationToken);
    SearchPlan Explain(SearchQuery query, PathFilter pathFilter);
    SearchStats Search(SearchQuery query, PathFilter pathFilter, IHitCollector collector, CancellationToken cancellationToken);
    MaterializedHit Materialize(in RawHit hit, int contextBefore, int contextAfter);

    /// <summary>Gets a document body owned by this snapshot.</summary>
    /// <remarks>The returned memory remains valid until this snapshot is disposed.</remarks>
    bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content);
}

public interface IOverlaySnapshot : IIndexSnapshot
{
    /// <remarks><paramref name="deletions"/> must remain unchanged until the returned task completes.</remarks>
    Task<UpdateStats> Update(IDocumentSource upserts, IReadOnlyList<DocumentKey> deletions, CancellationToken cancellationToken);

    /// <remarks><paramref name="deletions"/> must remain unchanged until the returned task completes.</remarks>
    Task<UpdateStats> Update(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayUpdateOptions options,
        CancellationToken cancellationToken);
    Task<UpdateStats> CommitToBase(ISiftIndex baseIndex, UpdateOptions options, CancellationToken cancellationToken);
    Task<UpdateStats> ApplyDeltaTo(ISiftIndex target, UpdateOptions options, CancellationToken cancellationToken);
}

public interface ISiftIndex : IDisposable
{
    bool RequiresRebuild { get; }
    IndexIdentity GetIndexIdentity();
    IndexState GetIndexState();
    IIndexSnapshot OpenSnapshot(SearchOptions options);
    IOverlaySnapshot OpenOverlaySnapshot(SearchOptions options);
    WriteActivityStats GetWriteActivityStats();
    IndexValidationStats GetValidationStats();
    MaintenanceStats GetMaintenanceStats();
    StorageStats GetStorageStats();
    // Reports whether the current generation contains work selectable by this policy.
    bool ShouldCompact(CompactOptions options);
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
