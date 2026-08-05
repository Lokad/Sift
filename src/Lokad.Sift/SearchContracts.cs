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
    MaxMatchedDocuments,
    MaxBytesScanned,
    MaxElapsedTime,
    RegexTimeout,
    CollectorStopped
}

public enum TimeoutPolicy
{
    StopSearch,
    Throw
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
            maxMatchedDocuments: int.MaxValue,
            maxBytesScanned: long.MaxValue,
            timeoutPolicy: TimeoutPolicy.StopSearch,
            maxHitsPerDocument: int.MaxValue,
            maxElapsedMilliseconds: 30_000)
    {
    }

    private SearchQuery(
        string pattern,
        PatternMode patternMode,
        CaseMode caseMode,
        bool wholeWord,
        bool wholeLine,
        bool multiline,
        bool dotAll,
        int maxHits,
        int maxMatchedDocuments,
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
        MaxMatchedDocuments = maxMatchedDocuments;
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
    public int MaxMatchedDocuments { get; init; }
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

    /// <summary>Gets the number of segments searched concurrently. Zero uses the default of one.</summary>
    public int SegmentParallelism { get; init; }
}

public sealed class PathFilter
{
    private IReadOnlyList<string> _includeGlobs = Array.Empty<string>();
    private IReadOnlyList<string> _excludeGlobs = Array.Empty<string>();

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
        IReadOnlyList<string>? includeGlobs,
        IReadOnlyList<string>? excludeGlobs,
        string? pathRegex)
    {
        PathPrefix = pathPrefix;
        _includeGlobs = includeGlobs is null
            ? Array.Empty<string>()
            : PublicResultCollection.CopyOf(includeGlobs);
        _excludeGlobs = excludeGlobs is null
            ? Array.Empty<string>()
            : PublicResultCollection.CopyOf(excludeGlobs);
        PathRegex = pathRegex;
    }

    public string? PathPrefix { get; init; }
    public IReadOnlyList<string> IncludeGlobs
    {
        get => _includeGlobs;
        init => _includeGlobs = PublicResultCollection.CopyOf(value);
    }

    public IReadOnlyList<string> ExcludeGlobs
    {
        get => _excludeGlobs;
        init => _excludeGlobs = PublicResultCollection.CopyOf(value);
    }

    public string? PathRegex { get; init; }
}

public readonly record struct RawHit
{
    private readonly object? _owner;

    internal RawHit(
        SegmentId segmentId,
        DocumentOrdinal documentOrdinal,
        int startByte,
        int endByte,
        int startLine,
        int endLine)
        : this(
            segmentId,
            documentOrdinal,
            startByte,
            endByte,
            startLine,
            endLine,
            owner: null)
    {
    }

    private RawHit(
        SegmentId segmentId,
        DocumentOrdinal documentOrdinal,
        int startByte,
        int endByte,
        int startLine,
        int endLine,
        object? owner)
    {
        SegmentId = segmentId;
        DocumentOrdinal = documentOrdinal;
        StartByte = startByte;
        EndByte = endByte;
        StartLine = startLine;
        EndLine = endLine;
        _owner = owner;
    }

    public SegmentId SegmentId { get; }
    public DocumentOrdinal DocumentOrdinal { get; }
    public int StartByte { get; }
    public int EndByte { get; }
    public int StartLine { get; }
    public int EndLine { get; }

    internal bool IsOwnedBy(object owner) => ReferenceEquals(_owner, owner);

    internal RawHit WithOwner(object owner) => new(
        SegmentId,
        DocumentOrdinal,
        StartByte,
        EndByte,
        StartLine,
        EndLine,
        owner);

    internal RawHit WithSegmentId(SegmentId segmentId) => new(
        segmentId,
        DocumentOrdinal,
        StartByte,
        EndByte,
        StartLine,
        EndLine,
        _owner);
}

public readonly record struct SelectedNgram(string Text, int ByteLength, int DocumentFrequency);

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
    TrigramIndex,
    Mixed
}

public enum SearchCandidateCombination
{
    None,
    Intersection,
    Union,
    BranchUnion,
    Mixed
}

public enum SearchVerifierKind
{
    Literal,
    Utf8Regex,
    Utf8RegexWithUnicodeFallback,
    RegexFallback,
    Mixed
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
        int segmentCount,
        int scopeDocumentCount,
        long scopeByteCount)
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
        ScopeDocumentCount = scopeDocumentCount;
        ScopeByteCount = scopeByteCount;
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
    public int ScopeDocumentCount { get; }
    public long ScopeByteCount { get; }
}

public sealed class SearchPlan
{
    internal SearchPlan(
        SearchPatternKind patternKind,
        CaseMode resolvedCaseMode,
        int scopeDocumentCount,
        long scopeByteCount,
        int segmentCount,
        string[] mandatoryFragments,
        string[] advisorySeeds,
        SelectedNgram[] selectedNgrams,
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
        PlannerAlternative[] plannerAlternatives)
    {
        PatternKind = patternKind;
        ResolvedCaseMode = resolvedCaseMode;
        ScopeDocumentCount = scopeDocumentCount;
        ScopeByteCount = scopeByteCount;
        SegmentCount = segmentCount;
        MandatoryFragments = PublicResultCollection.Own(mandatoryFragments);
        AdvisorySeeds = PublicResultCollection.Own(advisorySeeds);
        SelectedNgrams = PublicResultCollection.Own(selectedNgrams);
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
        PlannerAlternatives = PublicResultCollection.Own(plannerAlternatives);
    }

    public SearchPatternKind PatternKind { get; }
    public CaseMode ResolvedCaseMode { get; }
    public int ScopeDocumentCount { get; }
    public long ScopeByteCount { get; }
    public int SegmentCount { get; }
    public IReadOnlyList<string> MandatoryFragments { get; }
    public IReadOnlyList<string> AdvisorySeeds { get; }
    public IReadOnlyList<SelectedNgram> SelectedNgrams { get; }
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

    internal SearchStats WithStopReason(SearchStopReason stopReason)
    {
        return new SearchStats(
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
}

public sealed class MaterializedHit
{
    internal MaterializedHit(
        string path,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        string matchText,
        string[] beforeContext,
        string[] afterContext,
        bool isMatchTruncated,
        bool isContextTruncated)
    {
        Path = path;
        StartLine = startLine;
        StartColumn = startColumn;
        EndLine = endLine;
        EndColumn = endColumn;
        MatchText = matchText;
        BeforeContext = PublicResultCollection.Own(beforeContext);
        AfterContext = PublicResultCollection.Own(afterContext);
        IsMatchTruncated = isMatchTruncated;
        IsContextTruncated = isContextTruncated;
    }

    public string Path { get; }
    public int StartLine { get; }
    public int StartColumn { get; }
    public int EndLine { get; }
    public int EndColumn { get; }
    public string MatchText { get; }
    public IReadOnlyList<string> BeforeContext { get; }
    public IReadOnlyList<string> AfterContext { get; }
    public bool IsMatchTruncated { get; }
    public bool IsContextTruncated { get; }
}

public interface IHitCollector
{
    /// <remarks>
    ///     The callback runs within the search operation. It must not dispose the
    ///     producing snapshot or update the producing overlay before returning.
    /// </remarks>
    bool OnHit(in RawHit hit);
}
