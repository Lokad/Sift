using System.Text;

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

public enum RegexTimeoutPolicy
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
            regexTimeoutPolicy: RegexTimeoutPolicy.StopSearch,
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
        RegexTimeoutPolicy regexTimeoutPolicy,
        int maxHitsPerDocument,
        long maxElapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(pattern);
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
        RegexTimeoutPolicy = regexTimeoutPolicy;
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
    /// <summary>Gets the response to Sift's one-second safety timeout for each regex match operation.</summary>
    public RegexTimeoutPolicy RegexTimeoutPolicy { get; init; }
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
    /// <remarks>
    ///     A finite <see cref="SearchQuery.MaxBytesScanned"/> uses one segment lane
    ///     so the byte budget remains exact. Hit and matched-document limits are
    ///     enforced globally while parallel workers preserve manifest hit order.
    /// </remarks>
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
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _includeGlobs = PublicResultCollection.CopyOf(value);
        }
    }

    public IReadOnlyList<string> ExcludeGlobs
    {
        get => _excludeGlobs;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _excludeGlobs = PublicResultCollection.CopyOf(value);
        }
    }

    public string? PathRegex { get; init; }
}

/// <remarks>
///     Equality compares the public coordinates. The producing snapshot used for
///     materialization is a separate lifetime constraint.
/// </remarks>
public readonly struct RawHit : IEquatable<RawHit>
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

    public bool Equals(RawHit other) =>
        SegmentId == other.SegmentId &&
        DocumentOrdinal == other.DocumentOrdinal &&
        StartByte == other.StartByte &&
        EndByte == other.EndByte &&
        StartLine == other.StartLine &&
        EndLine == other.EndLine;

    public override bool Equals(object? obj) => obj is RawHit other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        SegmentId,
        DocumentOrdinal,
        StartByte,
        EndByte,
        StartLine,
        EndLine);

    public static bool operator ==(RawHit left, RawHit right) => left.Equals(right);
    public static bool operator !=(RawHit left, RawHit right) => !left.Equals(right);

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

public readonly record struct SelectedNgram
{
    internal SelectedNgram(uint packedValue, int byteLength, int documentFrequency)
    {
        PackedValue = packedValue;
        ByteLength = byteLength;
        DocumentFrequency = documentFrequency;
    }

    /// <summary>Gets the n-gram bytes packed in big-endian order.</summary>
    public uint PackedValue { get; }
    public int ByteLength { get; }
    public int DocumentFrequency { get; }

    /// <summary>Gets a best-effort UTF-8 rendering for diagnostics.</summary>
    /// <remarks>Use <see cref="PackedValue"/> and <see cref="ByteLength"/> as the lossless identity.</remarks>
    public string Text
    {
        get
        {
            Span<byte> bytes = stackalloc byte[ByteLength];
            WriteBytes(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>Gets a lossless hexadecimal rendering of the n-gram bytes.</summary>
    public string Hex
    {
        get
        {
            Span<byte> bytes = stackalloc byte[ByteLength];
            WriteBytes(bytes);
            return Convert.ToHexString(bytes);
        }
    }

    private void WriteBytes(Span<byte> destination)
    {
        var value = PackedValue;
        for (var i = ByteLength - 1; i >= 0; i--)
        {
            destination[i] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }
}

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

/// <summary>Describes one planner strategy observed across one or more segments.</summary>
/// <remarks>
///     An entry may cover fewer segments than the containing plan and is therefore
///     not necessarily an executable whole-snapshot alternative. Planner costs are
///     unitless heuristics comparable only within the same explanation.
/// </remarks>
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
    /// <summary>Gets the unitless estimated cost of direct scanning.</summary>
    public double EstimatedDirectScanCost { get; }
    /// <summary>Gets the unitless estimated indexed cost, when this strategy uses an index.</summary>
    public double? EstimatedIndexedCost { get; }
    /// <summary>Gets the estimated number of documents passed to this strategy's verifier.</summary>
    public double EstimatedCandidateDocuments { get; }
    /// <summary>Gets the selected unitless planner cost.</summary>
    public double EstimatedCost { get; }
    public int SegmentCount { get; }

    /// <summary>Gets the number of physical segment rows in this planning scope.</summary>
    /// <remarks>For overlays, this includes hidden base rows because visibility is applied after segment planning.</remarks>
    public int ScopeDocumentCount { get; }

    /// <summary>Gets the content bytes of the physical segment rows in this planning scope.</summary>
    /// <remarks>For overlays, this includes hidden base rows because visibility is applied after segment planning.</remarks>
    public long ScopeByteCount { get; }
}

/// <summary>Describes the diagnostic planner decision for one snapshot and query.</summary>
/// <remarks>
///     Cost values are implementation diagnostics, not elapsed-time predictions or
///     stable values across Sift versions.
/// </remarks>
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
        RegexTimeoutPolicy regexTimeoutPolicy,
        PlannerAlternative[] plannerAlternatives,
        SiftRegexRouteReason regexRouteReason = SiftRegexRouteReason.Literal)
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
        RegexTimeoutPolicy = regexTimeoutPolicy;
        PlannerAlternatives = PublicResultCollection.Own(plannerAlternatives);
        RegexRouteReason = regexRouteReason;
    }

    public SearchPatternKind PatternKind { get; }
    public CaseMode ResolvedCaseMode { get; }
    public SiftRegexRouteReason RegexRouteReason { get; }

    /// <summary>Gets the number of physical segment rows covered by the plan.</summary>
    /// <remarks>For overlays, this includes hidden base rows because visibility is applied after segment planning.</remarks>
    public int ScopeDocumentCount { get; }

    /// <summary>Gets the content bytes of the physical segment rows covered by the plan.</summary>
    /// <remarks>For overlays, this includes hidden base rows because visibility is applied after segment planning.</remarks>
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
    /// <summary>Gets whether content verification may need UTF-16 decoding in the physical path scope.</summary>
    /// <remarks>
    ///     Evaluated before content candidate pruning, overlay visibility and search limits.
    ///     A true value does not guarantee decoding occurs. Empty queries/scopes return false.
    ///     Hit display/context materialization is excluded.
    /// </remarks>
    public bool NeedsUnicodeDecode { get; }
    public bool MayRequireWholeDocumentVerification { get; }
    /// <summary>Gets the unitless estimated path-filter cost.</summary>
    public double EstimatedPathFilterCost { get; }
    /// <summary>Gets the unitless estimated direct-scan cost.</summary>
    public double EstimatedDirectScanCost { get; }
    /// <summary>Gets the unitless estimated indexed cost, when one is available.</summary>
    public double? EstimatedIndexedCost { get; }
    /// <summary>Gets the estimated number of documents passed to the selected verifier.</summary>
    public double EstimatedCandidateDocuments { get; }
    /// <summary>Gets the unitless estimated cost of the selected strategy.</summary>
    public double EstimatedCost { get; }
    public SearchCandidateKind CandidateKind { get; }
    public SearchCandidateCombination CandidateCombination { get; }
    public SearchVerifierKind VerifierKind { get; }
    /// <summary>Gets the next-best strategy that covers every segment, when available.</summary>
    public PlannerAlternative? RunnerUp { get; }
    /// <summary>Gets the runner-up's unitless estimated-cost excess over the selected strategy.</summary>
    public double? RunnerUpEstimatedCostDelta { get; }
    /// <summary>Gets the response to the per-match regex safety timeout.</summary>
    public RegexTimeoutPolicy RegexTimeoutPolicy { get; }
    /// <summary>Gets strategies observed during per-segment planning.</summary>
    /// <remarks>Use each entry's segment and scope counts to determine its coverage.</remarks>
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

    /// <summary>Gets the number of physical segment rows selected before visibility filtering.</summary>
    /// <remarks>For overlays, this includes hidden base rows so it describes search work rather than logical document count.</remarks>
    public int ScopeDocuments { get; }

    /// <summary>Gets the content bytes of physical segment rows selected before visibility filtering.</summary>
    /// <remarks>For overlays, this includes hidden base rows so it describes search work rather than logical content size.</remarks>
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
    /// <summary>Gets native prepared-query cache hits; counted once per logical search, including overlays.</summary>
    public long Utf8RegexCacheHits { get; }
    /// <summary>Gets native prepared-query cache misses; coalesced callers count as hits.</summary>
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

/// <summary>A materialized hit together with the complete line containing its start.</summary>
public sealed class MaterializedHitWithLine
{
    internal MaterializedHitWithLine(MaterializedHit hit, string matchingLine)
    {
        Hit = hit;
        MatchingLine = matchingLine;
    }

    public MaterializedHit Hit { get; }
    public string MatchingLine { get; }
}

public interface IHitCollector
{
    /// <remarks>
    ///     The callback runs within the search operation. It must not dispose the
    ///     producing snapshot or update the producing overlay before returning.
    ///     Collector exceptions propagate to the search caller unchanged; the
    ///     query's regex timeout policy does not classify collector failures.
    /// </remarks>
    bool OnHit(in RawHit hit);
}
