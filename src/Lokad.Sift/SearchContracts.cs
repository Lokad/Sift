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

    public SearchQuery(
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
            : PublicResultCollection.Freeze(includeGlobs);
        _excludeGlobs = excludeGlobs is null
            ? Array.Empty<string>()
            : PublicResultCollection.Freeze(excludeGlobs);
        PathRegex = pathRegex;
    }

    public string? PathPrefix { get; init; }
    public IReadOnlyList<string> IncludeGlobs
    {
        get => _includeGlobs;
        init => _includeGlobs = PublicResultCollection.Freeze(value);
    }

    public IReadOnlyList<string> ExcludeGlobs
    {
        get => _excludeGlobs;
        init => _excludeGlobs = PublicResultCollection.Freeze(value);
    }

    public string? PathRegex { get; init; }
}

public readonly record struct RawHit(
    ulong SegmentId,
    uint DocumentOrdinal,
    int StartByte,
    int EndByte,
    int StartLine,
    int EndLine);

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
    internal SearchPlan(SearchPlanData data)
    {
        PatternKind = data.PatternKind;
        ResolvedCaseMode = data.ResolvedCaseMode;
        ScopeDocumentCount = data.ScopeDocumentCount;
        ScopeByteCount = data.ScopeByteCount;
        SegmentCount = data.SegmentCount;
        MandatoryFragments = PublicResultCollection.Freeze(data.MandatoryFragments);
        AdvisorySeeds = PublicResultCollection.Freeze(data.AdvisorySeeds);
        SelectedNgrams = PublicResultCollection.Freeze(data.SelectedNgrams);
        UsesPathPrefixFilter = data.UsesPathPrefixFilter;
        UsesPathRegexFilter = data.UsesPathRegexFilter;
        UsesIncludeGlobs = data.UsesIncludeGlobs;
        UsesExcludeGlobs = data.UsesExcludeGlobs;
        UsesPathIndex = data.UsesPathIndex;
        UsesIndex = data.UsesIndex;
        CanUseLiteralVerifier = data.CanUseLiteralVerifier;
        CanUseNonBacktracking = data.CanUseNonBacktracking;
        NeedsUnicodeDecode = data.NeedsUnicodeDecode;
        MayRequireWholeDocumentVerification = data.MayRequireWholeDocumentVerification;
        EstimatedPathFilterCost = data.EstimatedPathFilterCost;
        EstimatedDirectScanCost = data.EstimatedDirectScanCost;
        EstimatedIndexedCost = data.EstimatedIndexedCost;
        EstimatedCandidateDocuments = data.EstimatedCandidateDocuments;
        EstimatedCost = data.EstimatedCost;
        CandidateKind = data.CandidateKind;
        CandidateCombination = data.CandidateCombination;
        VerifierKind = data.VerifierKind;
        RunnerUp = data.RunnerUp;
        RunnerUpEstimatedCostDelta = data.RunnerUpEstimatedCostDelta;
        TimeoutPolicy = data.TimeoutPolicy;
        PlannerAlternatives = PublicResultCollection.Freeze(data.PlannerAlternatives);
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

internal sealed class SearchPlanData
{
    public required SearchPatternKind PatternKind { get; init; }
    public required CaseMode ResolvedCaseMode { get; init; }
    public required int ScopeDocumentCount { get; init; }
    public required long ScopeByteCount { get; init; }
    public required int SegmentCount { get; init; }
    public required IReadOnlyList<string> MandatoryFragments { get; init; }
    public required IReadOnlyList<string> AdvisorySeeds { get; init; }
    public required IReadOnlyList<SelectedNgram> SelectedNgrams { get; init; }
    public required bool UsesPathPrefixFilter { get; init; }
    public required bool UsesPathRegexFilter { get; init; }
    public required bool UsesIncludeGlobs { get; init; }
    public required bool UsesExcludeGlobs { get; init; }
    public required bool UsesPathIndex { get; init; }
    public required bool UsesIndex { get; init; }
    public required bool CanUseLiteralVerifier { get; init; }
    public required bool CanUseNonBacktracking { get; init; }
    public required bool NeedsUnicodeDecode { get; init; }
    public required bool MayRequireWholeDocumentVerification { get; init; }
    public required double EstimatedPathFilterCost { get; init; }
    public required double EstimatedDirectScanCost { get; init; }
    public required double? EstimatedIndexedCost { get; init; }
    public required double EstimatedCandidateDocuments { get; init; }
    public required double EstimatedCost { get; init; }
    public required SearchCandidateKind CandidateKind { get; init; }
    public required SearchCandidateCombination CandidateCombination { get; init; }
    public required SearchVerifierKind VerifierKind { get; init; }
    public required PlannerAlternative? RunnerUp { get; init; }
    public required double? RunnerUpEstimatedCostDelta { get; init; }
    public required TimeoutPolicy TimeoutPolicy { get; init; }
    public required IReadOnlyList<PlannerAlternative> PlannerAlternatives { get; init; }
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
        IReadOnlyList<string> beforeContext,
        IReadOnlyList<string> afterContext,
        bool isMatchTruncated,
        bool isContextTruncated)
    {
        Path = path;
        StartLine = startLine;
        StartColumn = startColumn;
        EndLine = endLine;
        EndColumn = endColumn;
        MatchText = matchText;
        BeforeContext = PublicResultCollection.Freeze(beforeContext);
        AfterContext = PublicResultCollection.Freeze(afterContext);
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
    bool OnHit(in RawHit hit);
}
