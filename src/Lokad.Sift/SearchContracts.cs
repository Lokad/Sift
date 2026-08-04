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
    SegmentId SegmentId,
    DocumentOrdinal DocumentOrdinal,
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
    internal PlannerAlternative(in PlannerAlternativeData data)
    {
        CandidateKind = data.CandidateKind;
        CandidateCombination = data.CandidateCombination;
        VerifierKind = data.VerifierKind;
        UsesIndex = data.UsesIndex;
        EstimatedDirectScanCost = data.EstimatedDirectScanCost;
        EstimatedIndexedCost = data.EstimatedIndexedCost;
        EstimatedCandidateDocuments = data.EstimatedCandidateDocuments;
        EstimatedCost = data.EstimatedCost;
        SegmentCount = data.SegmentCount;
        ScopeDocumentCount = data.ScopeDocumentCount;
        ScopeByteCount = data.ScopeByteCount;
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

internal readonly struct PlannerAlternativeData
{
    public required SearchCandidateKind CandidateKind { get; init; }
    public required SearchCandidateCombination CandidateCombination { get; init; }
    public required SearchVerifierKind VerifierKind { get; init; }
    public required bool UsesIndex { get; init; }
    public required double EstimatedDirectScanCost { get; init; }
    public required double? EstimatedIndexedCost { get; init; }
    public required double EstimatedCandidateDocuments { get; init; }
    public required double EstimatedCost { get; init; }
    public required int SegmentCount { get; init; }
    public required int ScopeDocumentCount { get; init; }
    public required long ScopeByteCount { get; init; }
}

public sealed class SearchPlan
{
    internal SearchPlan(in SearchPlanData data)
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

internal readonly struct SearchPlanData
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
    internal SearchStats(in SearchStatsData data)
    {
        ElapsedMilliseconds = data.ElapsedMilliseconds;
        ScopeDocuments = data.ScopeDocuments;
        ScopeBytes = data.ScopeBytes;
        PostingsBytesRead = data.PostingsBytesRead;
        CandidateDocumentsAfterIndex = data.CandidateDocumentsAfterIndex;
        CandidateDocumentsAfterPrecheck = data.CandidateDocumentsAfterPrecheck;
        VerifiedDocuments = data.VerifiedDocuments;
        BytesScanned = data.BytesScanned;
        DecodedChars = data.DecodedChars;
        RegexInvocations = data.RegexInvocations;
        Utf8RegexInvocations = data.Utf8RegexInvocations;
        FallbackRegexInvocations = data.FallbackRegexInvocations;
        NonBacktrackingRegexInvocations = data.NonBacktrackingRegexInvocations;
        Utf8RegexMatchedDocuments = data.Utf8RegexMatchedDocuments;
        Utf8RegexMatchesEnumerated = data.Utf8RegexMatchesEnumerated;
        Utf8RegexCacheHits = data.Utf8RegexCacheHits;
        Utf8RegexCacheMisses = data.Utf8RegexCacheMisses;
        HitsReturned = data.HitsReturned;
        StopReason = data.StopReason;
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
        return new SearchStats(new SearchStatsData
        {
            ElapsedMilliseconds = ElapsedMilliseconds,
            ScopeDocuments = ScopeDocuments,
            ScopeBytes = ScopeBytes,
            PostingsBytesRead = PostingsBytesRead,
            CandidateDocumentsAfterIndex = CandidateDocumentsAfterIndex,
            CandidateDocumentsAfterPrecheck = CandidateDocumentsAfterPrecheck,
            VerifiedDocuments = VerifiedDocuments,
            BytesScanned = BytesScanned,
            DecodedChars = DecodedChars,
            RegexInvocations = RegexInvocations,
            Utf8RegexInvocations = Utf8RegexInvocations,
            FallbackRegexInvocations = FallbackRegexInvocations,
            NonBacktrackingRegexInvocations = NonBacktrackingRegexInvocations,
            Utf8RegexMatchedDocuments = Utf8RegexMatchedDocuments,
            Utf8RegexMatchesEnumerated = Utf8RegexMatchesEnumerated,
            Utf8RegexCacheHits = Utf8RegexCacheHits,
            Utf8RegexCacheMisses = Utf8RegexCacheMisses,
            HitsReturned = HitsReturned,
            StopReason = stopReason
        });
    }
}

internal readonly struct SearchStatsData
{
    public required long ElapsedMilliseconds { get; init; }
    public required int ScopeDocuments { get; init; }
    public required long ScopeBytes { get; init; }
    public required long PostingsBytesRead { get; init; }
    public required int CandidateDocumentsAfterIndex { get; init; }
    public required int CandidateDocumentsAfterPrecheck { get; init; }
    public required int VerifiedDocuments { get; init; }
    public required long BytesScanned { get; init; }
    public required long DecodedChars { get; init; }
    public required int RegexInvocations { get; init; }
    public required int Utf8RegexInvocations { get; init; }
    public required int FallbackRegexInvocations { get; init; }
    public required int NonBacktrackingRegexInvocations { get; init; }
    public required int Utf8RegexMatchedDocuments { get; init; }
    public required long Utf8RegexMatchesEnumerated { get; init; }
    public required long Utf8RegexCacheHits { get; init; }
    public required long Utf8RegexCacheMisses { get; init; }
    public required int HitsReturned { get; init; }
    public required SearchStopReason StopReason { get; init; }
}

public sealed class MaterializedHit
{
    internal MaterializedHit(in MaterializedHitData data)
    {
        Path = data.Path;
        StartLine = data.StartLine;
        StartColumn = data.StartColumn;
        EndLine = data.EndLine;
        EndColumn = data.EndColumn;
        MatchText = data.MatchText;
        BeforeContext = PublicResultCollection.Freeze(data.BeforeContext);
        AfterContext = PublicResultCollection.Freeze(data.AfterContext);
        IsMatchTruncated = data.IsMatchTruncated;
        IsContextTruncated = data.IsContextTruncated;
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

internal readonly struct MaterializedHitData
{
    public required string Path { get; init; }
    public required int StartLine { get; init; }
    public required int StartColumn { get; init; }
    public required int EndLine { get; init; }
    public required int EndColumn { get; init; }
    public required string MatchText { get; init; }
    public required IReadOnlyList<string> BeforeContext { get; init; }
    public required IReadOnlyList<string> AfterContext { get; init; }
    public required bool IsMatchTruncated { get; init; }
    public required bool IsContextTruncated { get; init; }
}

public interface IHitCollector
{
    bool OnHit(in RawHit hit);
}
