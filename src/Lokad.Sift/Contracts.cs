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
    PreserveExisting,
    DeleteExisting
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

public sealed record SearchQuery
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

    public string Pattern { get; }
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

public readonly record struct DocumentKey
{
    private readonly string? _path;

    public DocumentKey(string path)
    {
        _path = DocumentPath.Normalize(path);
    }

    public string Path => _path ?? throw new InvalidOperationException("The document key is uninitialized.");
}

public readonly record struct SubmittedDocument
{
    private readonly string? _path;

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
        _path = DocumentPath.Normalize(path);
        Content = content;
        LastModifiedUtc = lastModifiedUtc;
        ContentLifetime = contentLifetime;
    }

    public string Path => _path ?? throw new InvalidOperationException("The submitted document is uninitialized.");
    public ReadOnlyMemory<byte> Content { get; }
    public DateTimeOffset? LastModifiedUtc { get; }
    public DocumentContentLifetime ContentLifetime { get; }
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

public sealed record OverlayApplyOptions
{
    public OverlayApplyOptions()
        : this(RejectedUpsertPolicy.FailUpdate)
    {
    }

    public OverlayApplyOptions(RejectedUpsertPolicy rejectedUpsertPolicy)
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

public enum SearchPatternKind
{
    Literal,
    RegexLiteral,
    Utf8Regex,
    RegexFallback
}

public enum SearchCandidateKind
{
    DirectScan,
    Index,
    BigramIndex,
    TrigramIndex
}

public enum SearchCandidateCombination
{
    None,
    Intersection,
    Union,
    BranchUnion
}

public enum SearchVerifierKind
{
    Literal,
    Utf8Regex,
    Utf8RegexWithUnicodeFallback,
    RegexFallback
}

public sealed class PlannerAlternative
{
    internal PlannerAlternative(
        SearchCandidateKind candidateKind,
        SearchCandidateCombination candidateCombination,
        SearchVerifierKind verifierKind,
        bool usesIndex,
        double estimatedDirectScanCost,
        double? estimatedIndexedCost,
        double estimatedCandidateDocuments,
        double estimatedCost,
        int segmentCount)
    {
        CandidateKind = candidateKind;
        CandidateCombination = candidateCombination;
        VerifierKind = verifierKind;
        UsesIndex = usesIndex;
        EstimatedDirectScanCost = estimatedDirectScanCost;
        EstimatedIndexedCost = estimatedIndexedCost;
        EstimatedCandidateDocuments = estimatedCandidateDocuments;
        EstimatedCost = estimatedCost;
        SegmentCount = segmentCount;
    }

    public SearchCandidateKind CandidateKind { get; }
    public SearchCandidateCombination CandidateCombination { get; }
    public SearchVerifierKind VerifierKind { get; }
    public bool UsesIndex { get; }
    public double EstimatedDirectScanCost { get; }
    public double? EstimatedIndexedCost { get; }
    public double EstimatedCandidateDocuments { get; }
    public double EstimatedCost { get; }
    public int SegmentCount { get; }
}

public sealed class SearchPlan
{
    internal SearchPlan(
        SearchPatternKind patternKind,
        CaseMode resolvedCaseMode,
        int scopeDocumentCount,
        long scopeByteCount,
        IReadOnlyList<string> mandatoryFragments,
        IReadOnlyList<string> advisorySeeds,
        IReadOnlyList<SelectedNGram> selectedNgrams,
        bool usesPathPrefixFilter,
        bool usesPathRegexFilter,
        bool usesIncludeGlobs,
        bool usesExcludeGlobs,
        bool usesPathIndex,
        bool usesIndex,
        bool canUseLiteralVerifier,
        bool canUseNonBacktracking,
        bool needsUnicodeDecode,
        bool mayRequireWholeDocumentVerification,
        double estimatedPathFilterCost,
        double estimatedDirectScanCost,
        double? estimatedIndexedCost,
        double estimatedCandidateDocuments,
        double estimatedCost,
        SearchCandidateKind candidateKind,
        SearchCandidateCombination candidateCombination,
        SearchVerifierKind verifierKind,
        PlannerAlternative? runnerUp,
        double? runnerUpEstimatedCostDelta,
        TimeoutPolicy timeoutPolicy,
        IReadOnlyList<PlannerAlternative> plannerAlternatives)
    {
        PatternKind = patternKind;
        ResolvedCaseMode = resolvedCaseMode;
        ScopeDocumentCount = scopeDocumentCount;
        ScopeByteCount = scopeByteCount;
        MandatoryFragments = Freeze(mandatoryFragments);
        AdvisorySeeds = Freeze(advisorySeeds);
        SelectedNgrams = Freeze(selectedNgrams);
        UsesPathPrefixFilter = usesPathPrefixFilter;
        UsesPathRegexFilter = usesPathRegexFilter;
        UsesIncludeGlobs = usesIncludeGlobs;
        UsesExcludeGlobs = usesExcludeGlobs;
        UsesPathIndex = usesPathIndex;
        UsesIndex = usesIndex;
        CanUseLiteralVerifier = canUseLiteralVerifier;
        CanUseNonBacktracking = canUseNonBacktracking;
        NeedsUnicodeDecode = needsUnicodeDecode;
        MayRequireWholeDocumentVerification = mayRequireWholeDocumentVerification;
        EstimatedPathFilterCost = estimatedPathFilterCost;
        EstimatedDirectScanCost = estimatedDirectScanCost;
        EstimatedIndexedCost = estimatedIndexedCost;
        EstimatedCandidateDocuments = estimatedCandidateDocuments;
        EstimatedCost = estimatedCost;
        CandidateKind = candidateKind;
        CandidateCombination = candidateCombination;
        VerifierKind = verifierKind;
        RunnerUp = runnerUp;
        RunnerUpEstimatedCostDelta = runnerUpEstimatedCostDelta;
        TimeoutPolicy = timeoutPolicy;
        PlannerAlternatives = Freeze(plannerAlternatives);

        static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values)
        {
            var copy = new T[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                copy[i] = values[i];
            }

            return Array.AsReadOnly(copy);
        }
    }

    public SearchPatternKind PatternKind { get; }
    public CaseMode ResolvedCaseMode { get; }
    public int ScopeDocumentCount { get; }
    public long ScopeByteCount { get; }
    public IReadOnlyList<string> MandatoryFragments { get; }
    public IReadOnlyList<string> AdvisorySeeds { get; }
    public IReadOnlyList<SelectedNGram> SelectedNgrams { get; }
    public bool UsesPathPrefixFilter { get; }
    public bool UsesPathRegexFilter { get; }
    public bool UsesIncludeGlobs { get; }
    public bool UsesExcludeGlobs { get; }
    public bool UsesPathIndex { get; }
    public bool UsesIndex { get; }
    public bool CanUseLiteralVerifier { get; }
    public bool CanUseNonBacktracking { get; }
    public bool NeedsUnicodeDecode { get; }
    public bool MayRequireWholeDocumentVerification { get; }
    public double EstimatedPathFilterCost { get; }
    public double EstimatedDirectScanCost { get; }
    public double? EstimatedIndexedCost { get; }
    public double EstimatedCandidateDocuments { get; }
    public double EstimatedCost { get; }
    public SearchCandidateKind CandidateKind { get; }
    public SearchCandidateCombination CandidateCombination { get; }
    public SearchVerifierKind VerifierKind { get; }
    public PlannerAlternative? RunnerUp { get; }
    public double? RunnerUpEstimatedCostDelta { get; }
    public TimeoutPolicy TimeoutPolicy { get; }
    public IReadOnlyList<PlannerAlternative> PlannerAlternatives { get; }
}

public sealed class SearchStats
{
    internal SearchStats(
        long elapsedMilliseconds,
        int scopeDocuments,
        long scopeBytes,
        long postingsBytesRead,
        int candidateDocumentsAfterIndex,
        int candidateDocumentsAfterPrecheck,
        int verifiedDocuments,
        long bytesScanned,
        long decodedChars,
        int regexInvocations,
        int utf8RegexInvocations,
        int fallbackRegexInvocations,
        int nonBacktrackingRegexInvocations,
        int utf8RegexMatchedDocuments,
        long utf8RegexMatchesEnumerated,
        long utf8RegexCacheHits,
        long utf8RegexCacheMisses,
        int hitsReturned,
        SearchStopReason stopReason)
    {
        ElapsedMilliseconds = elapsedMilliseconds;
        ScopeDocuments = scopeDocuments;
        ScopeBytes = scopeBytes;
        PostingsBytesRead = postingsBytesRead;
        CandidateDocumentsAfterIndex = candidateDocumentsAfterIndex;
        CandidateDocumentsAfterPrecheck = candidateDocumentsAfterPrecheck;
        VerifiedDocuments = verifiedDocuments;
        BytesScanned = bytesScanned;
        DecodedChars = decodedChars;
        RegexInvocations = regexInvocations;
        Utf8RegexInvocations = utf8RegexInvocations;
        FallbackRegexInvocations = fallbackRegexInvocations;
        NonBacktrackingRegexInvocations = nonBacktrackingRegexInvocations;
        Utf8RegexMatchedDocuments = utf8RegexMatchedDocuments;
        Utf8RegexMatchesEnumerated = utf8RegexMatchesEnumerated;
        Utf8RegexCacheHits = utf8RegexCacheHits;
        Utf8RegexCacheMisses = utf8RegexCacheMisses;
        HitsReturned = hitsReturned;
        StopReason = stopReason;
    }

    public long ElapsedMilliseconds { get; }
    public int ScopeDocuments { get; }
    public long ScopeBytes { get; }
    public long PostingsBytesRead { get; }
    public int CandidateDocumentsAfterIndex { get; }
    public int CandidateDocumentsAfterPrecheck { get; }
    public int VerifiedDocuments { get; }
    public long BytesScanned { get; }
    public long DecodedChars { get; }
    public int RegexInvocations { get; }
    public int Utf8RegexInvocations { get; }
    public int FallbackRegexInvocations { get; }
    public int NonBacktrackingRegexInvocations { get; }
    public int Utf8RegexMatchedDocuments { get; }
    public long Utf8RegexMatchesEnumerated { get; }
    public long Utf8RegexCacheHits { get; }
    public long Utf8RegexCacheMisses { get; }
    public int HitsReturned { get; }
    public SearchStopReason StopReason { get; }

    internal SearchStats WithStopReason(SearchStopReason stopReason) => new(
        ElapsedMilliseconds,
        ScopeDocuments,
        ScopeBytes,
        PostingsBytesRead,
        CandidateDocumentsAfterIndex,
        CandidateDocumentsAfterPrecheck,
        VerifiedDocuments,
        BytesScanned,
        DecodedChars,
        RegexInvocations,
        Utf8RegexInvocations,
        FallbackRegexInvocations,
        NonBacktrackingRegexInvocations,
        Utf8RegexMatchedDocuments,
        Utf8RegexMatchesEnumerated,
        Utf8RegexCacheHits,
        Utf8RegexCacheMisses,
        HitsReturned,
        stopReason);
}

public sealed record BuildStats
{
    internal BuildStats(
        int submittedDocuments,
        int indexedDocuments,
        int rejectedDocuments,
        int segmentCount,
        long submittedBytes,
        long contentBytes,
        long rejectedBytes,
        long elapsedMilliseconds,
        long peakManagedBytes,
        IReadOnlyList<RejectedDocument> rejectedDocumentDetails,
        IndexIdentity committedIdentity)
    {
        SubmittedDocuments = submittedDocuments;
        IndexedDocuments = indexedDocuments;
        RejectedDocuments = rejectedDocuments;
        SegmentCount = segmentCount;
        SubmittedBytes = submittedBytes;
        ContentBytes = contentBytes;
        RejectedBytes = rejectedBytes;
        ElapsedMilliseconds = elapsedMilliseconds;
        PeakManagedBytes = peakManagedBytes;
        RejectedDocumentDetails = Freeze(rejectedDocumentDetails);
        CommittedIdentity = committedIdentity;
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

    internal BuildStats WithPeakManagedBytes(long peakManagedBytes) => new(
        SubmittedDocuments,
        IndexedDocuments,
        RejectedDocuments,
        SegmentCount,
        SubmittedBytes,
        ContentBytes,
        RejectedBytes,
        ElapsedMilliseconds,
        peakManagedBytes,
        RejectedDocumentDetails,
        CommittedIdentity);

    internal static IReadOnlyList<RejectedDocument> Freeze(IReadOnlyList<RejectedDocument> documents)
    {
        var copy = new RejectedDocument[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            copy[i] = documents[i];
        }

        return Array.AsReadOnly(copy);
    }
}

public sealed record UpdateStats
{
    internal UpdateStats(
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
        IReadOnlyList<RejectedDocument> rejectedDocuments,
        long elapsedMilliseconds,
        IndexIdentity committedIdentity)
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
        RejectedDocuments = BuildStats.Freeze(rejectedDocuments);
        ElapsedMilliseconds = elapsedMilliseconds;
        CommittedIdentity = committedIdentity;
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
    public IReadOnlyList<RejectedDocument> RejectedDocuments { get; }
    public long ElapsedMilliseconds { get; }

    /// <summary>Identity of the index state returned by the completed update.</summary>
    /// <remarks>
    ///     This is set on the final result returned by <c>Update</c>. The provisional
    ///     statistics passed to <see cref="UpdateOptions.CommitMetadataProvider"/>
    ///     have the default identity because the manifest has not yet been committed.
    /// </remarks>
    public IndexIdentity CommittedIdentity { get; }
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

public sealed record CompactStats
{
    internal CompactStats(
        int inputSegments,
        int outputSegments,
        int mergedDocuments,
        long contentBytes,
        long elapsedMilliseconds,
        long peakManagedBytes,
        bool hasMoreWork)
    {
        InputSegments = inputSegments;
        OutputSegments = outputSegments;
        MergedDocuments = mergedDocuments;
        ContentBytes = contentBytes;
        ElapsedMilliseconds = elapsedMilliseconds;
        PeakManagedBytes = peakManagedBytes;
        HasMoreWork = hasMoreWork;
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

    internal CompactStats WithPeakManagedBytes(long peakManagedBytes) => new(
        InputSegments,
        OutputSegments,
        MergedDocuments,
        ContentBytes,
        ElapsedMilliseconds,
        peakManagedBytes,
        HasMoreWork);
}

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

public readonly record struct IndexIdentity(
    // Changes whenever a full build replaces the logical corpus.
    Guid CorpusId,
    // Changes only when the visible document set or content changes.
    ulong ContentRevision,
    // Changes whenever a manifest is published, including compaction and metadata-only commits.
    ulong StorageGeneration);

public readonly record struct IndexState(
    IndexIdentity Identity,
    ReadOnlyMemory<byte> CommitMetadata);

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

public sealed record MaintenanceStats
{
    internal MaintenanceStats(
        ulong generation,
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

public sealed record StorageStats
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
        ulong? oldestLeasedGeneration,
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

public interface IDocumentKeyCollector
{
    bool OnDocument(in DocumentKey key);
}

public readonly record struct DocumentVisitStats(
    int VisitedDocuments,
    bool Completed);

public interface IDocumentSource
{
    bool TryReadNext(out SubmittedDocument document);
}

public interface IIndexSnapshot : IDisposable
{
    DocumentVisitStats VisitDocuments(IDocumentKeyCollector collector, CancellationToken cancellationToken);
    SearchPlan Explain(in SearchQuery query, in PathFilter pathFilter);
    SearchStats Search(in SearchQuery query, in PathFilter pathFilter, IHitCollector collector, CancellationToken cancellationToken);
    MaterializedHit Materialize(in RawHit hit, int contextBefore, int contextAfter);
    bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content);
}

public interface IOverlaySnapshot : IIndexSnapshot
{
    Task<UpdateStats> Apply(IDocumentSource upserts, IReadOnlyList<DocumentKey> deletions, CancellationToken cancellationToken);
    Task<UpdateStats> Apply(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayApplyOptions options,
        CancellationToken cancellationToken);
    Task<UpdateStats> ApplyTo(ISift target, UpdateOptions options, CancellationToken cancellationToken);
}

public interface ISift : IDisposable
{
    bool RequiresRebuild { get; }
    IndexIdentity GetIndexIdentity();
    IndexState GetIndexState();
    IIndexSnapshot OpenSnapshot(SearchOptions options);
    IOverlaySnapshot OpenOverlaySnapshot(SearchOptions options);
    ActivityStats GetActivityStats();
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
    Task<UpdateStats> Update(IDocumentSource upserts, IReadOnlyList<DocumentKey> deletions, CancellationToken cancellationToken);
    Task<UpdateStats> Update(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken);
    Task<UpdateStats> Update(
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions,
        CancellationToken cancellationToken);
    Task<UpdateStats> Update(
        IAsyncEnumerable<SubmittedDocument> upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken);
    Task<CompactStats> Compact(CompactOptions options, CancellationToken cancellationToken);
}

