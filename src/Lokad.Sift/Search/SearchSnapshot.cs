using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Lokad.Sift;
using Lokad.Sift.Indexer;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal sealed class SearchSnapshot : IIndexSnapshot
{
    internal const int PreparedQueryCacheCapacity = 256;
    internal const int PreparedQueryCacheCharacterCapacity = 256 * 1024;
    private static readonly BoundedLruCache<PreparedQueryKey, PreparedSearchQuery> PreparedQueryCache = new(
        PreparedQueryCacheCapacity,
        PreparedQueryCacheCharacterCapacity,
        static key => key.Pattern.Length);
    private const int OrderedAnchorPrecheckCandidateLimit = 3000;
    private const int ConstrainedOrderedAnchorPrecheckCandidateLimit = 50000;
    private const int MaxConstrainedGapSearchBytes = 512;
    private const int ParallelHitBufferCapacity = 256;
    private readonly IReadOnlyList<SegmentManifestEntry> _segments;
    private readonly ISiftStorage _storage;
    private readonly SiftOptions _options;
    private readonly SearchOptions _searchOptions;
    private readonly StorageReadSnapshot _storageSnapshot;
    private readonly object _mappedSegmentsSync = new();
    // Reuse mappings across search, path lookup, and hit materialization while the generation is pinned.
    private readonly Dictionary<ulong, MappedSegment> _mappedSegments = new();
    // Keep the generation lease alive until every operation that may open one
    // of its segment packs has completed.
    // Collectors may materialize hits while Search holds its disposal-exclusion read lease.
    private readonly ReaderWriterLockSlim _lifetimeLock = new(LockRecursionPolicy.SupportsRecursion);
    private Action? _onDisposed;
    private readonly Action<Exception>? _onIndexFailure;
    private int _disposed;
    internal static int PreparedQueryCacheCount => PreparedQueryCache.Count;
    internal static int PreparedQueryCacheCharacters => PreparedQueryCache.Weight;

    public SearchSnapshot(
        ISiftStorage storage,
        SiftOptions options,
        SearchOptions searchOptions,
        Action? onDisposed,
        Action<Exception>? onIndexFailure)
    {
        if (searchOptions.SegmentParallelism < 0 ||
            searchOptions.SegmentParallelism > options.MaxSegmentParallelism)
        {
            throw new ArgumentOutOfRangeException(
                nameof(searchOptions),
                $"SegmentParallelism must be between zero and {options.MaxSegmentParallelism}.");
        }

        _storage = storage;
        _options = options;
        _searchOptions = searchOptions.SegmentParallelism == 0
            ? new SearchOptions(segmentParallelism: 1)
            : searchOptions;
        _onDisposed = onDisposed;
        _onIndexFailure = onIndexFailure;
        _storageSnapshot = storage.OpenReadSnapshot();
        // A snapshot pins the manifest generation, not every segment mapping.
        // Search operations open only the segment packs they are actively using.
        _segments = _storageSnapshot.Manifest.Segments;
    }

    public DocumentVisitStats VisitDocuments(IDocumentKeyCollector collector)
    {
        return VisitDocuments(collector, CancellationToken.None);
    }

    public DocumentVisitStats VisitDocuments(
        IDocumentKeyCollector collector,
        CancellationToken cancellationToken)
    {
        var guardedCollector = new GuardedDocumentKeyCollector(collector);
        try
        {
            using var operation = BeginOperation();
            var visitedDocuments = 0;
            foreach (var entry in _segments)
            {
                var cache = OpenSegment(entry);
                for (uint ordinal = 0; ordinal < (uint)cache.Segment.DocumentCount; ordinal++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsDeadOrdinal(cache.DeadOrdinals, ordinal))
                    {
                        continue;
                    }

                    var document = cache.Segment.GetDocument(ordinal);
                    var key = new DocumentKey(cache.Segment.GetPath(document));
                    visitedDocuments++;
                    if (!guardedCollector.OnDocument(key))
                    {
                        return new DocumentVisitStats(visitedDocuments, Completed: false);
                    }
                }
            }

            return new DocumentVisitStats(visitedDocuments, Completed: true);
        }
        catch (CollectorFailureException exception)
        {
            exception.DispatchInfo.Throw();
            throw;
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            ReportIndexFailure(exception);
            throw;
        }
    }

    public SearchPlan Explain(SearchQuery query, PathFilter pathFilter)
    {
        ValidateQuery(query);
        ValidatePathFilter(pathFilter);
        try
        {
            return ExplainCore(query, pathFilter);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            ReportIndexFailure(exception);
            throw;
        }
    }

    private SearchPlan ExplainCore(SearchQuery query, PathFilter pathFilter)
    {
        using var operation = BeginOperation();
        var preparedQuery = PrepareSearchQuery(query);
        var resolvedCaseMode = preparedQuery.ResolvedCaseMode;
        var matcher = PathFilterMatcher.Prepare(pathFilter);
        var regexPlan = preparedQuery.RegexPlan;
        var anchors = preparedQuery.Anchors;
        var mandatoryFragments = preparedQuery.MandatoryFragments;
        var advisorySeeds = preparedQuery.AdvisorySeeds;
        var scopeDocuments = 0;
        long scopeBytes = 0;
        var selected = new Dictionary<(string Text, int ByteLength), int>();
        var usesIndex = false;
        var usesUnicodeFallback = false;
        SearchCandidateKind? candidateKind = null;
        SearchCandidateCombination? candidateCombination = null;
        SearchVerifierKind? verifierKind = null;
        var estimatedPathFilterCost = 0d;
        var estimatedDirectScanCost = 0d;
        double? estimatedIndexedCost = null;
        var estimatedCandidateDocuments = 0d;
        var estimatedCost = 0d;
        var segmentCount = 0;
        var alternativeSums = new Dictionary<(SearchCandidateKind CandidateKind, SearchCandidateCombination CandidateCombination, SearchVerifierKind VerifierKind, bool UsesIndex), PlannerAlternativeAccumulator>();

        foreach (var entry in _segments)
        {
            var cache = OpenSegment(entry);
            var segmentPlan = SearchPlanner.CreateSegmentPlan(
                cache,
                matcher,
                query,
                resolvedCaseMode,
                anchors,
                regexPlan,
                _options,
                CancellationToken.None);
            scopeDocuments += segmentPlan.Scope.Count;
            scopeBytes += segmentPlan.Scope.Bytes;
            segmentCount++;
            var chosenPlan = segmentPlan.ContentPlan.ChosenPlan;
            usesUnicodeFallback |=
                chosenPlan.VerifierKind ==
                SearchVerifierKind.Utf8RegexWithUnicodeFallback;
            usesIndex |= chosenPlan.CandidatePlan.UsesIndex;
            candidateKind = Merge(candidateKind, chosenPlan.CandidateKind, SearchCandidateKind.Mixed);
            candidateCombination = Merge(
                candidateCombination,
                chosenPlan.CandidateCombination,
                SearchCandidateCombination.Mixed);
            verifierKind = Merge(verifierKind, chosenPlan.VerifierKind, SearchVerifierKind.Mixed);
            estimatedPathFilterCost += segmentPlan.EstimatedPathFilterCost;
            estimatedDirectScanCost += chosenPlan.EstimatedDirectScanCost;
            if (chosenPlan.EstimatedIndexedCost is double indexedCost)
            {
                estimatedIndexedCost = (estimatedIndexedCost ?? 0d) + indexedCost;
            }

            estimatedCandidateDocuments += chosenPlan.EstimatedCandidateDocuments;
            estimatedCost += chosenPlan.EstimatedCost;
            foreach (var gram in chosenPlan.SelectedNgrams)
            {
                var key = (gram.Text, gram.ByteLength);
                selected.TryGetValue(key, out var documentFrequency);
                selected[key] = checked(documentFrequency + gram.DocumentFrequency);
            }

            foreach (var alternative in segmentPlan.ContentPlan.Alternatives)
            {
                var key = (alternative.CandidateKind, alternative.CandidateCombination, alternative.VerifierKind, alternative.CandidatePlan.UsesIndex);
                ref var accumulator = ref CollectionsMarshal.GetValueRefOrAddDefault(alternativeSums, key, out _);
                accumulator.Add(alternative, segmentPlan.Scope);
            }
        }

        var patternKind = SearchPlanKinds.GetPatternKind(query, regexPlan);
        var canUseLiteralVerifier = query.PatternMode == PatternMode.Literal || regexPlan.IsExactLiteral;
        var canUseNonBacktracking = query.PatternMode == PatternMode.Regex && regexPlan.CanUseNonBacktracking;
        var needsUnicodeDecode =
            RegexExecutionPlanner.NeedsUnicodeDecode(query, mandatoryFragments, resolvedCaseMode, regexPlan) ||
            usesUnicodeFallback;
        var mayRequireWholeDocumentVerification = query.PatternMode == PatternMode.Regex && !regexPlan.IsExactLiteral || query.WholeLine || query.Multiline;
        var selectedNgrams = selected
            .Select(static pair => new SelectedNgram(
                pair.Key.Text,
                pair.Key.ByteLength,
                pair.Value))
            .OrderBy(static gram => gram.ByteLength)
            .ThenBy(static gram => gram.Text, StringComparer.Ordinal)
            .ToArray();
        var resolvedCandidateKind = candidateKind ?? SearchPlanner.GetCandidateKind(selectedNgrams, usesIndex);
        var resolvedCandidateCombination = candidateCombination ??
            SearchPlanner.GetCandidateCombination(selectedNgrams, usesIndex, unionCandidates: false, branchUnion: false);
        var resolvedVerifierKind = verifierKind ?? SearchPlanKinds.GetVerifierKind(query, regexPlan);
        var plannerAlternatives = alternativeSums
            .Select(static pair => pair.Value.ToPlannerAlternative(pair.Key.CandidateKind, pair.Key.CandidateCombination, pair.Key.VerifierKind, pair.Key.UsesIndex))
            .OrderBy(static alternative => alternative.EstimatedCost)
            .ThenBy(static alternative => alternative.EstimatedCandidateDocuments)
            .ToArray();
        var runnerUp = plannerAlternatives
            .FirstOrDefault(alternative =>
                alternative.SegmentCount == segmentCount &&
                (alternative.CandidateKind != resolvedCandidateKind ||
                 alternative.CandidateCombination != resolvedCandidateCombination ||
                 alternative.VerifierKind != resolvedVerifierKind));

        return new SearchPlan(
            patternKind,
            resolvedCaseMode,
            scopeDocuments,
            scopeBytes,
            segmentCount,
            mandatoryFragments,
            advisorySeeds,
            selectedNgrams,
            matcher.PathPrefix is not null,
            !string.IsNullOrWhiteSpace(pathFilter.PathRegex),
            pathFilter.IncludeGlobs.Count > 0,
            pathFilter.ExcludeGlobs.Count > 0,
            matcher.UsesPathIndex,
            usesIndex,
            canUseLiteralVerifier,
            canUseNonBacktracking,
            needsUnicodeDecode,
            mayRequireWholeDocumentVerification,
            estimatedPathFilterCost,
            estimatedDirectScanCost,
            estimatedIndexedCost,
            estimatedCandidateDocuments,
            estimatedCost,
            resolvedCandidateKind,
            resolvedCandidateCombination,
            resolvedVerifierKind,
            runnerUp,
            runnerUp is null ? null : Math.Max(0d, runnerUp.EstimatedCost - estimatedCost),
            query.PatternMode == PatternMode.Regex ? query.TimeoutPolicy : TimeoutPolicy.StopSearch,
            plannerAlternatives);

        static T Merge<T>(T? aggregate, T value, T mixed)
            where T : struct, Enum
        {
            return aggregate is null || EqualityComparer<T>.Default.Equals(aggregate.Value, value)
                ? value
                : mixed;
        }
    }

    public SearchStats Search(SearchQuery query, PathFilter pathFilter, IHitCollector collector)
    {
        return Search(query, pathFilter, collector, CancellationToken.None);
    }

    public SearchStats Search(
        SearchQuery query,
        PathFilter pathFilter,
        IHitCollector collector,
        CancellationToken cancellationToken)
    {
        return Search(query, pathFilter, collector, pathVisibility: null, cancellationToken);
    }

    internal SearchStats Search(
        SearchQuery query,
        PathFilter pathFilter,
        IHitCollector collector,
        IPathVisibility? pathVisibility,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        ValidatePathFilter(pathFilter);
        var guardedCollector = new GuardedHitCollector(collector);
        try
        {
            return SearchCore(
                query,
                pathFilter,
                guardedCollector,
                pathVisibility,
                cancellationToken);
        }
        catch (CollectorFailureException exception)
        {
            exception.DispatchInfo.Throw();
            throw;
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            ReportIndexFailure(exception);
            throw;
        }
    }

    private SearchStats SearchCore(
        SearchQuery query,
        PathFilter pathFilter,
        IHitCollector collector,
        IPathVisibility? pathVisibility,
        CancellationToken cancellationToken)
    {
        using var operation = BeginOperation();
        var watch = Stopwatch.StartNew();
        var preparedQuery = PrepareSearchQuery(query);
        var matcher = PathFilterMatcher.Prepare(pathFilter);
        using var deadlineCancellation = CreateDeadlineCancellation(
            query.MaxElapsedMilliseconds,
            cancellationToken);
        var effectiveCancellationToken = deadlineCancellation?.Token ?? cancellationToken;
        return CanUseSegmentParallelSearch(query)
            ? SearchParallel(
                query,
                matcher,
                preparedQuery,
                collector,
                pathVisibility,
                effectiveCancellationToken,
                cancellationToken,
                watch)
            : SearchSequential(
                query,
                matcher,
                preparedQuery,
                collector,
                pathVisibility,
                effectiveCancellationToken,
                cancellationToken,
                watch);
    }

    private SearchStats SearchSequential(
        SearchQuery query,
        PathFilterMatcher matcher,
        PreparedSearchQuery preparedQuery,
        IHitCollector collector,
        IPathVisibility? pathVisibility,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken,
        Stopwatch watch)
    {
        var resolvedCaseMode = preparedQuery.ResolvedCaseMode;
        var regexPlan = preparedQuery.RegexPlan;
        var preparedRegex = preparedQuery.GetRegexExecution();
        var anchors = preparedQuery.Anchors;
        var mandatoryFragments = preparedQuery.MandatoryFragments;
        var preparedLiteral = preparedQuery.Literal;
        var preparedMandatoryFragments = preparedQuery.MandatoryFragmentsForPrecheck;
        var preparedOrderedAnchorGroups = preparedQuery.OrderedAnchorGroups;
        var scopeDocuments = 0;
        long scopeBytes = 0;
        long postingsBytesRead = 0;
        var candidateDocumentsAfterIndex = 0;
        var candidateDocumentsAfterPrecheck = 0;
        var verifiedDocuments = 0;
        long bytesScanned = 0;
        long decodedChars = 0;
        var regexInvocations = 0;
        var byteRegexInvocations = 0;
        var fallbackRegexInvocations = 0;
        var nonBacktrackingRegexInvocations = 0;
        var byteRegexMatchedDocuments = 0;
        long byteRegexStartPositionsTried = 0;
        long byteRegexCacheHits = preparedRegex.Utf8Regex?.CacheHit == true ? 1 : 0;
        long byteRegexCacheMisses = preparedRegex.Utf8Regex?.CacheHit == false ? 1 : 0;
        var hitsReturned = 0;
        var hitDocuments = 0;
        var stopReason = SearchStopReason.None;

        try
        {
            if (HasElapsed(query, watch))
            {
                stopReason = SearchStopReason.MaxElapsedTime;
                goto Done;
            }

            foreach (var entry in _segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasElapsed(query, watch))
                {
                    stopReason = SearchStopReason.MaxElapsedTime;
                    break;
                }

                var cache = OpenSegment(entry, cancellationToken);
                var segmentPlan = SearchPlanner.CreateSegmentPlan(
                    cache,
                    matcher,
                    query,
                    resolvedCaseMode,
                    anchors,
                    regexPlan,
                    _options,
                    cancellationToken);
                scopeDocuments += segmentPlan.Scope.Count;
                scopeBytes += segmentPlan.Scope.Bytes;

                var candidates = CandidateGenerator.Generate(cache.Segment, segmentPlan.Scope.Ordinals, segmentPlan.ContentPlan.ChosenPlan.CandidatePlan);
                if (ShouldIncludeNonAsciiRegexCandidates(query, resolvedCaseMode, regexPlan, segmentPlan.ContentPlan.ChosenPlan))
                {
                    candidates = CandidateGenerator.IncludeNonAsciiDocuments(cache.Segment, segmentPlan.Scope.Ordinals, candidates);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var byteRegexStartPrecheckGroups = segmentPlan.ContentPlan.ChosenPlan.CandidateKind == SearchCandidateKind.DirectScan
                    ? preparedOrderedAnchorGroups
                    : [];
                postingsBytesRead += candidates.PostingsBytesRead;
                candidateDocumentsAfterIndex += candidates.Ordinals.Count;
                var useOrderedAnchorPrecheck = ShouldUseOrderedAnchorPrecheck(preparedOrderedAnchorGroups, candidates.Ordinals.Count);

                foreach (var ordinal in candidates.Ordinals)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (HasElapsed(query, watch))
                    {
                        stopReason = SearchStopReason.MaxElapsedTime;
                        goto Done;
                    }

                    var document = cache.Segment.GetDocument(ordinal);
                    if (pathVisibility is not null && !pathVisibility.IsVisible(cache.Segment.GetPathBytes(document)))
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(query.Pattern))
                    {
                        candidateDocumentsAfterPrecheck++;
                        verifiedDocuments++;
                        var shouldContinue = collector.OnHit(
                            new RawHit(cache.Segment.SegmentId, document.Ordinal, 0, 0, 1, 1));
                        hitsReturned++;
                        if (!shouldContinue)
                        {
                            stopReason = SearchStopReason.CollectorStopped;
                            goto Done;
                        }

                        hitDocuments++;
                    }
                    else
                    {
                        if (document.ContentLength > query.MaxBytesScanned - bytesScanned)
                        {
                            stopReason = SearchStopReason.MaxBytesScanned;
                            goto Done;
                        }

                        bytesScanned += document.ContentLength;
                        var contentBytes = cache.Segment.GetContentBytes(document);
                        var needsUnicodeCaseFoldVerification =
                            query.PatternMode == PatternMode.Regex &&
                            resolvedCaseMode == CaseMode.Insensitive &&
                            !CanUseUtf8RegexForDocument(document);
                        string? predecodedText = null;
                        if (!needsUnicodeCaseFoldVerification &&
                            !PassesMandatoryPrecheck(contentBytes, mandatoryFragments, preparedMandatoryFragments, resolvedCaseMode, cancellationToken, ref decodedChars, out predecodedText))
                        {
                            continue;
                        }

                        if (!needsUnicodeCaseFoldVerification &&
                            useOrderedAnchorPrecheck &&
                            !PassesOrderedAnchorPrecheck(contentBytes, preparedOrderedAnchorGroups, cancellationToken))
                        {
                            continue;
                        }

                        candidateDocumentsAfterPrecheck++;
                        var countBefore = hitsReturned;
                        var documentHitLimit = GetDocumentHitLimit(hitsReturned, query);
                        if (query.PatternMode == PatternMode.Literal || regexPlan.IsExactLiteral)
                        {
                            SearchLiteralDocument(cache.Segment, document, query, resolvedCaseMode, preparedLiteral, documentHitLimit, collector, cancellationToken, ref decodedChars, ref hitsReturned, ref stopReason, predecodedText, regexPlan.ExactLiteral);
                        }
                        else if (preparedRegex.Utf8Regex is { } utf8Regex && CanUseUtf8RegexForDocument(document))
                        {
                            Utf8RegexEngine.TrySearchDocument(cache.Segment, document, utf8Regex, byteRegexStartPrecheckGroups, documentHitLimit, collector, ref hitsReturned, ref byteRegexInvocations, ref byteRegexMatchedDocuments, ref byteRegexStartPositionsTried, ref stopReason);
                        }
                        else if (preparedRegex.FallbackRegex is { } fallbackRegex)
                        {
                            RegexVerifier.TrySearchDocument(cache.Segment, document, fallbackRegex, documentHitLimit, collector, ref decodedChars, ref hitsReturned, ref fallbackRegexInvocations, ref nonBacktrackingRegexInvocations, ref stopReason, predecodedText);
                        }
                        else
                        {
                            throw new InvalidOperationException("The regex execution plan has no prepared verifier.");
                        }

                        verifiedDocuments++;
                        regexInvocations = byteRegexInvocations + fallbackRegexInvocations;
                        if (stopReason != SearchStopReason.None)
                        {
                            goto Done;
                        }

                        if (hitsReturned > countBefore)
                        {
                            hitDocuments++;
                        }
                    }

                    if (hitDocuments >= query.MaxDocuments)
                    {
                        stopReason = SearchStopReason.MaxDocuments;
                        goto Done;
                    }

                    if (hitsReturned >= query.MaxHits)
                    {
                        stopReason = SearchStopReason.MaxHits;
                        goto Done;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (
            !callerCancellationToken.IsCancellationRequested &&
            cancellationToken.IsCancellationRequested)
        {
            stopReason = SearchStopReason.MaxElapsedTime;
        }
        catch (RegexMatchTimeoutException) when (query.TimeoutPolicy != TimeoutPolicy.Throw)
        {
            stopReason = SearchStopReason.RegexTimeout;
        }

    Done:
        watch.Stop();
        return new SearchStats(
            watch.ElapsedMilliseconds,
            scopeDocuments,
            scopeBytes,
            postingsBytesRead,
            candidateDocumentsAfterIndex,
            candidateDocumentsAfterPrecheck,
            verifiedDocuments,
            bytesScanned,
            decodedChars,
            regexInvocations,
            byteRegexInvocations,
            fallbackRegexInvocations,
            nonBacktrackingRegexInvocations,
            byteRegexMatchedDocuments,
            byteRegexStartPositionsTried,
            byteRegexCacheHits,
            byteRegexCacheMisses,
            hitsReturned,
            stopReason);
    }

    private SearchStats SearchParallel(
        SearchQuery query,
        PathFilterMatcher matcher,
        PreparedSearchQuery preparedQuery,
        IHitCollector collector,
        IPathVisibility? pathVisibility,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken,
        Stopwatch watch)
    {
        var resolvedCaseMode = preparedQuery.ResolvedCaseMode;
        var regexPlan = preparedQuery.RegexPlan;
        var preparedRegex = preparedQuery.GetRegexExecution();
        var anchors = preparedQuery.Anchors;
        var preparedLiteral = preparedQuery.Literal;
        var preparedMandatoryFragments = preparedQuery.MandatoryFragmentsForPrecheck;
        var preparedOrderedAnchorGroups = preparedQuery.OrderedAnchorGroups;
        var queryCopy = query;

        var scopeDocuments = 0;
        long scopeBytes = 0;
        long postingsBytesRead = 0;
        var candidateDocumentsAfterIndex = 0;
        var candidateDocumentsAfterPrecheck = 0;
        var verifiedDocuments = 0;
        long bytesScanned = 0;
        long decodedChars = 0;
        var regexInvocations = 0;
        var byteRegexInvocations = 0;
        var fallbackRegexInvocations = 0;
        var nonBacktrackingRegexInvocations = 0;
        var byteRegexMatchedDocuments = 0;
        long byteRegexStartPositionsTried = 0;
        long byteRegexCacheHits = preparedRegex.Utf8Regex?.CacheHit == true ? 1 : 0;
        long byteRegexCacheMisses = preparedRegex.Utf8Regex?.CacheHit == false ? 1 : 0;
        var hitsReturned = 0;
        var stopReason = SearchStopReason.None;

        if (HasElapsed(query, watch))
        {
            stopReason = SearchStopReason.MaxElapsedTime;
            goto Done;
        }

        try
        {
            // Producers may plan and verify concurrently, while bounded queues let
            // the caller consume hits in stable manifest order.
            var batchCapacity = Math.Min(_searchOptions.SegmentParallelism, _segments.Count);
            for (var batchStart = 0; batchStart < _segments.Count; batchStart += batchCapacity)
            {
                if (HasElapsed(query, watch))
                {
                    stopReason = SearchStopReason.MaxElapsedTime;
                    goto Done;
                }

                var batchLength = Math.Min(batchCapacity, _segments.Count - batchStart);
                using (var batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var batchResults = new SegmentSearchResult[batchLength];
                    var batchFailures = new Exception?[batchLength];
                    var hitBuffers = new BlockingCollection<RawHit>[batchLength];
                    var workers = new Task[batchLength];
                    for (var batchIndex = 0; batchIndex < batchLength; batchIndex++)
                    {
                        var segmentIndex = batchStart + batchIndex;
                        var resultIndex = batchIndex;
                        var hitBuffer = new BlockingCollection<RawHit>(ParallelHitBufferCapacity);
                        hitBuffers[resultIndex] = hitBuffer;
                        workers[resultIndex] = Task.Run(() =>
                        {
                            try
                            {
                                var cache = OpenSegment(
                                    _segments[segmentIndex],
                                    batchCancellation.Token);
                                var bufferedCollector = new BoundedHitCollector(
                                    hitBuffer,
                                    batchCancellation.Token);
                                batchResults[resultIndex] = SearchSegmentFully(
                                    cache,
                                    queryCopy,
                                    matcher,
                                    resolvedCaseMode,
                                    anchors,
                                    preparedLiteral,
                                    preparedMandatoryFragments,
                                    preparedOrderedAnchorGroups,
                                    regexPlan,
                                    preparedRegex,
                                    pathVisibility,
                                    watch,
                                    bufferedCollector,
                                    batchCancellation.Token,
                                    cancellationToken);
                            }
                            catch (OperationCanceledException) when (
                                batchCancellation.IsCancellationRequested &&
                                !cancellationToken.IsCancellationRequested)
                            {
                                // The ordering thread cancels the private batch token when
                                // the caller collector asks the search to stop.
                            }
                            catch (Exception exception)
                            {
                                // Transport worker failures to the ordering thread without
                                // leaving another bounded producer permanently blocked.
                                batchFailures[resultIndex] = exception;
                            }
                            finally
                            {
                                hitBuffer.CompleteAdding();
                            }
                        });
                    }

                    try
                    {
                        for (var batchIndex = 0; batchIndex < batchLength; batchIndex++)
                        {
                            foreach (var hit in hitBuffers[batchIndex].GetConsumingEnumerable(cancellationToken))
                            {
                                var shouldContinue = collector.OnHit(hit);
                                hitsReturned++;
                                if (!shouldContinue)
                                {
                                    stopReason = SearchStopReason.CollectorStopped;
                                    batchCancellation.Cancel();
                                    break;
                                }
                            }

                            workers[batchIndex].GetAwaiter().GetResult();
                            if (batchFailures[batchIndex] is { } failure)
                            {
                                ExceptionDispatchInfo.Capture(failure).Throw();
                            }

                            var result = batchResults[batchIndex];
                            scopeDocuments += result.ScopeDocuments;
                            scopeBytes += result.ScopeBytes;
                            postingsBytesRead += result.PostingsBytesRead;
                            candidateDocumentsAfterIndex += result.CandidateDocumentsAfterIndex;
                            candidateDocumentsAfterPrecheck += result.CandidateDocumentsAfterPrecheck;
                            verifiedDocuments += result.VerifiedDocuments;
                            bytesScanned += result.BytesScanned;
                            decodedChars += result.DecodedChars;
                            regexInvocations += result.RegexInvocations;
                            byteRegexInvocations += result.Utf8RegexInvocations;
                            fallbackRegexInvocations += result.FallbackRegexInvocations;
                            nonBacktrackingRegexInvocations += result.NonBacktrackingRegexInvocations;
                            byteRegexMatchedDocuments += result.Utf8RegexMatchedDocuments;
                            byteRegexStartPositionsTried += result.Utf8RegexMatchesEnumerated;
                            byteRegexCacheHits += result.Utf8RegexCacheHits;
                            byteRegexCacheMisses += result.Utf8RegexCacheMisses;

                            if (stopReason != SearchStopReason.None)
                            {
                                goto Done;
                            }

                            if (result.StopReason != SearchStopReason.None)
                            {
                                stopReason = result.StopReason;
                                goto Done;
                            }

                            if (HasElapsed(query, watch))
                            {
                                stopReason = SearchStopReason.MaxElapsedTime;
                                goto Done;
                            }
                        }
                    }
                    finally
                    {
                        batchCancellation.Cancel();
                        Task.WaitAll(workers);
                        for (var batchIndex = 0; batchIndex < batchLength; batchIndex++)
                        {
                            hitBuffers[batchIndex].Dispose();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (
            !callerCancellationToken.IsCancellationRequested &&
            cancellationToken.IsCancellationRequested)
        {
            stopReason = SearchStopReason.MaxElapsedTime;
        }

    Done:
        watch.Stop();
        return new SearchStats(
            watch.ElapsedMilliseconds,
            scopeDocuments,
            scopeBytes,
            postingsBytesRead,
            candidateDocumentsAfterIndex,
            candidateDocumentsAfterPrecheck,
            verifiedDocuments,
            bytesScanned,
            decodedChars,
            regexInvocations,
            byteRegexInvocations,
            fallbackRegexInvocations,
            nonBacktrackingRegexInvocations,
            byteRegexMatchedDocuments,
            byteRegexStartPositionsTried,
            byteRegexCacheHits,
            byteRegexCacheMisses,
            hitsReturned,
            stopReason);
    }

    public MaterializedHit Materialize(in RawHit hit, int contextBefore, int contextAfter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contextBefore);
        ArgumentOutOfRangeException.ThrowIfNegative(contextAfter);
        try
        {
            return MaterializeCore(hit, contextBefore, contextAfter);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            ReportIndexFailure(exception);
            throw;
        }
    }

    private MaterializedHit MaterializeCore(in RawHit hit, int contextBefore, int contextAfter)
    {
        using var operation = BeginOperation();
        var cache = OpenSegment(hit.SegmentId);

        var document = cache.Segment.GetDocument(hit.DocumentOrdinal);
        var path = cache.Segment.GetPath(document);
        var contentBytes = cache.Segment.GetContentBytes(document);
        var encodedLineIndex = cache.Segment.GetEncodedLineIndexBytes(document);
        if (hit.StartByte < 0 || hit.EndByte < hit.StartByte || hit.EndByte > contentBytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(hit), "Hit byte bounds are outside the document.");
        }

        var startLine = hit.StartLine > 0 ? hit.StartLine : LineMap.GetLineFromByteOffset(encodedLineIndex, hit.StartByte);
        var endLine = hit.EndLine > 0 ? hit.EndLine : LineMap.GetLineFromByteOffset(encodedLineIndex, Math.Max(hit.StartByte, hit.EndByte == 0 ? hit.StartByte : hit.EndByte - 1));
        var asciiDocument = (document.Flags & (1 << 1)) != 0;
        var startColumn = GetUtf16Column(contentBytes, encodedLineIndex, hit.StartByte, startLine, asciiDocument);
        var endColumn = GetUtf16Column(contentBytes, encodedLineIndex, hit.EndByte, endLine, asciiDocument);
        var materializedMatchEnd = hit.StartByte +
            Math.Min(hit.EndByte - hit.StartByte, _options.MaxMaterializedMatchBytes);
        if (materializedMatchEnd < hit.EndByte && !asciiDocument)
        {
            // A byte ceiling can land inside a UTF-8 scalar; retain only complete text.
            while (materializedMatchEnd > hit.StartByte &&
                   (contentBytes[materializedMatchEnd] & 0xC0) == 0x80)
            {
                materializedMatchEnd--;
            }
        }

        var isMatchTruncated = materializedMatchEnd < hit.EndByte;
        var matchText = materializedMatchEnd > hit.StartByte
            ? DecodeText(contentBytes[hit.StartByte..materializedMatchEnd], asciiDocument, trimLineEnd: false)
            : string.Empty;
        var contextLineBudget = _options.MaxMaterializedContextLines;
        var contextByteBudget = _options.MaxMaterializedContextBytes;
        var beforeStartLine = (int)Math.Max(1L, (long)startLine - contextBefore);
        var afterStartLine = endLine == int.MaxValue ? int.MaxValue : endLine + 1;
        var afterEndLine = (int)Math.Min(int.MaxValue, (long)endLine + contextAfter);
        var beforeContext = CollectContext(
            contentBytes,
            encodedLineIndex,
            beforeStartLine,
            startLine - 1,
            asciiDocument,
            ref contextLineBudget,
            ref contextByteBudget,
            out var beforeContextTruncated);
        var afterContext = CollectContext(
            contentBytes,
            encodedLineIndex,
            afterStartLine,
            afterEndLine,
            asciiDocument,
            ref contextLineBudget,
            ref contextByteBudget,
            out var afterContextTruncated);

        return new MaterializedHit(
            path,
            startLine,
            startColumn,
            endLine,
            endColumn,
            matchText,
            beforeContext,
            afterContext,
            isMatchTruncated,
            beforeContextTruncated || afterContextTruncated);
    }

    public bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content)
    {
        try
        {
            return TryGetDocumentBytesCore(key, out content);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            ReportIndexFailure(exception);
            throw;
        }
    }

    private bool TryGetDocumentBytesCore(DocumentKey key, out ReadOnlyMemory<byte> content)
    {
        using var operation = BeginOperation();
        var normalizedPath = key.Path;
        if (!TryFindLiveDocument(normalizedPath, out var cache, out var ordinal))
        {
            content = default;
            return false;
        }

        var document = cache.Segment.GetDocument(ordinal);
        content = cache.Segment.GetContentMemory(document);
        return true;
    }

    internal bool ContainsDocument(DocumentKey key)
    {
        try
        {
            using var operation = BeginOperation();
            var normalizedPath = key.Path;
            return TryFindLiveDocument(normalizedPath, out _, out _);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            ReportIndexFailure(exception);
            throw;
        }
    }

    private bool TryFindLiveDocument(
        string normalizedPath,
        out SegmentCache cache,
        out uint ordinal)
    {
        foreach (var entry in _segments)
        {
            cache = OpenSegment(entry);
            if (cache.Segment.TryFindDocumentOrdinal(normalizedPath, out ordinal) &&
                !IsDeadOrdinal(cache.DeadOrdinals, ordinal))
            {
                return true;
            }
        }

        cache = default;
        ordinal = default;
        return false;
    }

    internal string GetPath(in RawHit hit)
    {
        try
        {
            return GetPathCore(hit);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            ReportIndexFailure(exception);
            throw;
        }
    }

    private string GetPathCore(in RawHit hit)
    {
        using var operation = BeginOperation();
        var cache = OpenSegment(hit.SegmentId);
        var document = cache.Segment.GetDocument(hit.DocumentOrdinal);
        return cache.Segment.GetPath(document);
    }

    internal ulong[] GetSegmentIds()
    {
        using var operation = BeginOperation();
        var ids = GC.AllocateUninitializedArray<ulong>(_segments.Count);
        for (var i = 0; i < _segments.Count; i++)
        {
            ids[i] = _segments[i].SegmentId;
        }

        return ids;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        _lifetimeLock.EnterWriteLock();
        try
        {
            foreach (var segment in _mappedSegments.Values)
            {
                segment.Dispose();
            }
            _mappedSegments.Clear();
            _storageSnapshot.Dispose();
            Interlocked.Exchange(ref _onDisposed, null)?.Invoke();
        }
        finally
        {
            _lifetimeLock.ExitWriteLock();
        }
    }

    private SegmentCache OpenSegment(ulong segmentId)
    {
        foreach (var candidate in _segments)
        {
            if (candidate.SegmentId == segmentId)
            {
                return OpenSegment(candidate);
            }
        }

        throw new InvalidOperationException($"Unknown segment id {segmentId}.");
    }

    private SegmentCache OpenSegment(SegmentManifestEntry entry)
    {
        return OpenSegment(entry, CancellationToken.None);
    }

    private SegmentCache OpenSegment(
        SegmentManifestEntry entry,
        CancellationToken cancellationToken)
    {
        MappedSegment? openedSegment = null;
        try
        {
            lock (_mappedSegmentsSync)
            {
                if (_mappedSegments.TryGetValue(entry.SegmentId, out var mappedSegment))
                {
                    return CreateCache(mappedSegment, entry);
                }
            }

            openedSegment = _storage.OpenSegment(entry.SegmentId, cancellationToken);
            lock (_mappedSegmentsSync)
            {
                if (_mappedSegments.TryGetValue(entry.SegmentId, out var mappedSegment))
                {
                    return CreateCache(mappedSegment, entry);
                }

                _mappedSegments.Add(entry.SegmentId, openedSegment);
                var cache = CreateCache(openedSegment, entry);
                openedSegment = null;
                return cache;
            }
        }
        finally
        {
            // A concurrent operation may have populated the cache while this pack was being opened.
            openedSegment?.Dispose();
        }

        static SegmentCache CreateCache(MappedSegment segment, SegmentManifestEntry manifestEntry)
        {
            var nonAscii = segment.GetNonAsciiDocumentStats();
            var liveNonAsciiDocuments = nonAscii.DocumentCount;
            var liveNonAsciiBytes = nonAscii.ContentBytes;
            foreach (var deadOrdinal in manifestEntry.DeadOrdinals)
            {
                var deadDocument = segment.GetDocument(deadOrdinal);
                if ((deadDocument.Flags & (1 << 1)) == 0)
                {
                    liveNonAsciiDocuments--;
                    liveNonAsciiBytes -= deadDocument.ContentLength;
                }
            }

            return new SegmentCache(
                segment,
                manifestEntry.DeadOrdinals,
                manifestEntry.ContentBytes - manifestEntry.DeadContentBytes,
                liveNonAsciiDocuments,
                liveNonAsciiBytes);
        }
    }

    private SnapshotOperation BeginOperation()
    {
        _lifetimeLock.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            return new SnapshotOperation(_lifetimeLock);
        }
        catch
        {
            _lifetimeLock.ExitReadLock();
            throw;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private void ReportIndexFailure(Exception exception)
    {
        _onIndexFailure?.Invoke(exception);
    }

    private static bool IsIndexCorruption(Exception exception)
    {
        return exception is InvalidDataException or
            FileNotFoundException or
            DirectoryNotFoundException or
            EndOfStreamException;
    }

    private void ValidateQuery(SearchQuery query)
    {
        if (!Enum.IsDefined(query.PatternMode))
        {
            throw new ArgumentOutOfRangeException(nameof(query), "PatternMode is invalid.");
        }

        if (!Enum.IsDefined(query.CaseMode))
        {
            throw new ArgumentOutOfRangeException(nameof(query), "CaseMode is invalid.");
        }

        if (!Enum.IsDefined(query.TimeoutPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(query), "TimeoutPolicy is invalid.");
        }

        if (query.Pattern.Length > _options.MaxPatternCharacters)
        {
            throw new ArgumentException(
                $"Search pattern exceeds the {_options.MaxPatternCharacters}-character limit.",
                nameof(query));
        }

        if (query.PatternMode == PatternMode.Regex &&
            RegexStructure.ExceedsNestingLimit(query.Pattern, _options.MaxPatternNesting))
        {
            throw new ArgumentException(
                $"Search pattern exceeds the {_options.MaxPatternNesting}-level nesting limit.",
                nameof(query));
        }

        if (query.MaxHits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "MaxHits must be positive.");
        }

        if (query.MaxDocuments <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "MaxDocuments must be positive.");
        }

        if (query.MaxBytesScanned <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "MaxBytesScanned must be positive.");
        }

        if (query.MaxHitsPerDocument <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "MaxHitsPerDocument must be positive.");
        }

        if (query.MaxElapsedMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "MaxElapsedMilliseconds must be positive.");
        }
    }

    private void ValidatePathFilter(PathFilter pathFilter)
    {
        long totalCharacters = 0;
        ValidatePathPattern(pathFilter.PathPrefix);
        ValidatePathPattern(pathFilter.PathRegex);
        if (pathFilter.PathRegex is { } pathRegex &&
            RegexStructure.ExceedsNestingLimit(pathRegex, _options.MaxPatternNesting))
        {
            throw new ArgumentException(
                $"Path regex exceeds the {_options.MaxPatternNesting}-level nesting limit.",
                nameof(pathFilter));
        }

        var includeCount = pathFilter.IncludeGlobs.Count;
        var excludeCount = pathFilter.ExcludeGlobs.Count;
        if ((long)includeCount + excludeCount > _options.MaxPathFilterPatterns)
        {
            throw new ArgumentException(
                $"Path filter exceeds the {_options.MaxPathFilterPatterns}-pattern limit.",
                nameof(pathFilter));
        }

        var includes = pathFilter.IncludeGlobs;
        for (var i = 0; i < includes.Count; i++)
        {
            ValidatePathPattern(includes[i]);
        }

        var excludes = pathFilter.ExcludeGlobs;
        for (var i = 0; i < excludes.Count; i++)
        {
            ValidatePathPattern(excludes[i]);
        }

        if (totalCharacters > _options.MaxPathFilterCharacters)
        {
            throw new ArgumentException(
                $"Path filter exceeds the {_options.MaxPathFilterCharacters}-character aggregate limit.",
                nameof(pathFilter));
        }

        void ValidatePathPattern(string? pattern)
        {
            if (pattern is null)
            {
                return;
            }

            totalCharacters += pattern.Length;
            if (pattern.Length > _options.MaxPatternCharacters)
            {
                throw new ArgumentException(
                    $"Path pattern exceeds the {_options.MaxPatternCharacters}-character limit.",
                    nameof(pathFilter));
            }
        }
    }

    private static int GetDocumentHitLimit(int hitsReturned, SearchQuery query)
    {
        return (int)Math.Min(
            query.MaxHits,
            Math.Min(int.MaxValue, (long)hitsReturned + query.MaxHitsPerDocument));
    }

    private static bool HasElapsed(SearchQuery query, Stopwatch watch)
    {
        return watch.ElapsedMilliseconds >= query.MaxElapsedMilliseconds;
    }

    private static CancellationTokenSource? CreateDeadlineCancellation(
        long maxElapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        if (maxElapsedMilliseconds > uint.MaxValue - 1L)
        {
            return null;
        }

        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(maxElapsedMilliseconds));
        return deadline;
    }

    private static bool IsDeadOrdinal(uint[] deadOrdinals, uint ordinal)
    {
        return Array.BinarySearch(deadOrdinals, ordinal) >= 0;
    }

    private SegmentSearchResult SearchSegmentFully(
        SegmentCache cache,
        SearchQuery query,
        PathFilterMatcher matcher,
        CaseMode resolvedCaseMode,
        in SearchQueryAnchors anchors,
        PreparedLiteralQuery? preparedLiteral,
        PreparedMandatoryFragment[] preparedMandatoryFragments,
        PreparedOrderedAnchor[][] preparedOrderedAnchorGroups,
        RegexExecutionPlan regexPlan,
        PreparedRegexExecution preparedRegex,
        IPathVisibility? pathVisibility,
        Stopwatch watch,
        IHitCollector collector,
        CancellationToken cancellationToken,
        CancellationToken operationCancellationToken)
    {
        var segmentPlan = SearchPlanner.CreateSegmentPlan(
            cache,
            matcher,
            query,
            resolvedCaseMode,
            anchors,
            regexPlan,
            _options,
            cancellationToken);
        var scope = segmentPlan.Scope;
        var mandatoryFragments = anchors.GetMandatoryTexts();
        var postingsBytesRead = 0L;
        var candidateDocumentsAfterIndex = 0;
        var candidates = CandidateGenerator.Generate(cache.Segment, scope.Ordinals, segmentPlan.ContentPlan.ChosenPlan.CandidatePlan);
        if (ShouldIncludeNonAsciiRegexCandidates(query, resolvedCaseMode, regexPlan, segmentPlan.ContentPlan.ChosenPlan))
        {
            candidates = CandidateGenerator.IncludeNonAsciiDocuments(cache.Segment, scope.Ordinals, candidates);
        }
        var byteRegexStartPrecheckGroups = segmentPlan.ContentPlan.ChosenPlan.CandidateKind == SearchCandidateKind.DirectScan
            ? preparedOrderedAnchorGroups
            : [];
        postingsBytesRead = candidates.PostingsBytesRead;
        candidateDocumentsAfterIndex = candidates.Ordinals.Count;
        var useOrderedAnchorPrecheck = ShouldUseOrderedAnchorPrecheck(preparedOrderedAnchorGroups, candidates.Ordinals.Count);
        var segmentStopReason = SearchStopReason.None;
        long segmentBytesScanned = 0;
        long decodedChars = 0;
        var candidateDocumentsAfterPrecheck = 0;
        var verifiedDocuments = 0;
        var byteRegexInvocations = 0;
        var fallbackRegexInvocations = 0;
        var nonBacktrackingRegexInvocations = 0;
        var byteRegexMatchedDocuments = 0;
        long byteRegexStartPositionsTried = 0;
        long byteRegexCacheHits = 0;
        long byteRegexCacheMisses = 0;
        var hitsReturned = 0;
        var segmentHitDocuments = 0;

        foreach (var ordinal in candidates.Ordinals)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                operationCancellationToken.ThrowIfCancellationRequested();
                segmentStopReason = SearchStopReason.CollectorStopped;
                break;
            }

            if (HasElapsed(query, watch))
            {
                segmentStopReason = SearchStopReason.MaxElapsedTime;
                break;
            }

            var document = cache.Segment.GetDocument(ordinal);
            if (pathVisibility is not null && !pathVisibility.IsVisible(cache.Segment.GetPathBytes(document)))
            {
                continue;
            }

            if (string.IsNullOrEmpty(query.Pattern))
            {
                var shouldContinue = collector.OnHit(
                    new RawHit(cache.Segment.SegmentId, document.Ordinal, 0, 0, 1, 1));
                candidateDocumentsAfterPrecheck++;
                verifiedDocuments++;
                hitsReturned++;
                segmentHitDocuments++;
                if (!shouldContinue)
                {
                    segmentStopReason = SearchStopReason.CollectorStopped;
                    break;
                }

                if (segmentHitDocuments >= query.MaxDocuments)
                {
                    segmentStopReason = SearchStopReason.MaxDocuments;
                    break;
                }

                continue;
            }

            if (document.ContentLength > query.MaxBytesScanned - segmentBytesScanned)
            {
                segmentStopReason = SearchStopReason.MaxBytesScanned;
                break;
            }

            segmentBytesScanned += document.ContentLength;
            var contentBytes = cache.Segment.GetContentBytes(document);
            var needsUnicodeCaseFoldVerification =
                query.PatternMode == PatternMode.Regex &&
                resolvedCaseMode == CaseMode.Insensitive &&
                !CanUseUtf8RegexForDocument(document);
            string? predecodedText = null;
            if (!needsUnicodeCaseFoldVerification &&
                !PassesMandatoryPrecheck(contentBytes, mandatoryFragments, preparedMandatoryFragments, resolvedCaseMode, operationCancellationToken, ref decodedChars, out predecodedText))
            {
                continue;
            }

            if (!needsUnicodeCaseFoldVerification &&
                useOrderedAnchorPrecheck &&
                !PassesOrderedAnchorPrecheck(contentBytes, preparedOrderedAnchorGroups, operationCancellationToken))
            {
                continue;
            }

            candidateDocumentsAfterPrecheck++;
            verifiedDocuments++;
            var countBefore = hitsReturned;
            var documentHitLimit = GetDocumentHitLimit(hitsReturned, query);
            var documentStopReason = SearchStopReason.None;
            if (query.PatternMode == PatternMode.Literal || regexPlan.IsExactLiteral)
            {
                SearchLiteralDocument(cache.Segment, document, query, resolvedCaseMode, preparedLiteral, documentHitLimit, collector, operationCancellationToken, ref decodedChars, ref hitsReturned, ref documentStopReason, predecodedText, regexPlan.ExactLiteral);
            }
            else if (preparedRegex.Utf8Regex is { } utf8Regex && CanUseUtf8RegexForDocument(document))
            {
                Utf8RegexEngine.TrySearchDocument(cache.Segment, document, utf8Regex, byteRegexStartPrecheckGroups, documentHitLimit, collector, ref hitsReturned, ref byteRegexInvocations, ref byteRegexMatchedDocuments, ref byteRegexStartPositionsTried, ref documentStopReason);
            }
            else if (preparedRegex.FallbackRegex is { } fallbackRegex)
            {
                RegexVerifier.TrySearchDocument(cache.Segment, document, fallbackRegex, documentHitLimit, collector, ref decodedChars, ref hitsReturned, ref fallbackRegexInvocations, ref nonBacktrackingRegexInvocations, ref documentStopReason, predecodedText);
            }
            else
            {
                throw new InvalidOperationException("The regex execution plan has no prepared verifier.");
            }
            if (documentStopReason == SearchStopReason.None && hitsReturned >= query.MaxHits)
            {
                documentStopReason = SearchStopReason.MaxHits;
            }

            if (hitsReturned > countBefore)
            {
                segmentHitDocuments++;
            }

            if (documentStopReason != SearchStopReason.None)
            {
                segmentStopReason = documentStopReason;
                break;
            }

            if (segmentHitDocuments >= query.MaxDocuments)
            {
                segmentStopReason = SearchStopReason.MaxDocuments;
                break;
            }
        }

        return new SegmentSearchResult(
            scope.Count,
            scope.Bytes,
            postingsBytesRead,
            candidateDocumentsAfterIndex,
            candidateDocumentsAfterPrecheck,
            verifiedDocuments,
            segmentBytesScanned,
            decodedChars,
            byteRegexInvocations + fallbackRegexInvocations,
            byteRegexInvocations,
            fallbackRegexInvocations,
            nonBacktrackingRegexInvocations,
            byteRegexMatchedDocuments,
            byteRegexStartPositionsTried,
            byteRegexCacheHits,
            byteRegexCacheMisses,
            segmentStopReason);
    }

    private static void SearchLiteralDocument(
        MappedSegment segment,
        SegmentDocument document,
        SearchQuery query,
        CaseMode resolvedCaseMode,
        PreparedLiteralQuery? preparedLiteral,
        int hitLimit,
        IHitCollector collector,
        CancellationToken cancellationToken,
        ref long decodedChars,
        ref int hitsReturned,
        ref SearchStopReason stopReason,
        string? predecodedText,
        string? literalOverride)
    {
        var contentBytes = segment.GetContentBytes(document);
        var encodedLineIndex = segment.GetEncodedLineIndexBytes(document);
        var pattern = literalOverride ?? query.Pattern;
        if (string.IsNullOrEmpty(pattern))
        {
            var shouldContinue = collector.OnHit(new RawHit(segment.SegmentId, document.Ordinal, 0, 0, 1, 1));
            hitsReturned++;
            if (!shouldContinue)
            {
                stopReason = SearchStopReason.CollectorStopped;
            }

            return;
        }

        var needle = preparedLiteral?.Needle ?? Encoding.UTF8.GetBytes(pattern);
        var asciiNeedle = preparedLiteral?.IsAscii ?? needle.AsSpan().IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0;
        var asciiDocument = query.WholeWord && asciiNeedle && contentBytes.IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0;

        if (resolvedCaseMode == CaseMode.Sensitive &&
            asciiNeedle &&
            (!query.WholeWord && !query.WholeLine || asciiDocument || query.WholeLine))
        {
            SearchAsciiLiteralDocument(segment, document, encodedLineIndex, contentBytes, needle, [], query.WholeWord && asciiDocument, query.WholeLine, caseSensitive: true, hitLimit, collector, cancellationToken, ref hitsReturned, ref stopReason);
            return;
        }

        if (resolvedCaseMode == CaseMode.Insensitive &&
            asciiNeedle &&
            (!query.WholeWord && !query.WholeLine || asciiDocument || query.WholeLine))
        {
            var loweredNeedle = preparedLiteral?.LoweredAsciiNeedle ?? LowerAscii(needle);
            var prefixTable = preparedLiteral?.AsciiPrefixTable ?? BuildPrefixTable(loweredNeedle);
            SearchAsciiLiteralDocument(segment, document, encodedLineIndex, contentBytes, loweredNeedle, prefixTable, query.WholeWord && asciiDocument, query.WholeLine, caseSensitive: false, hitLimit, collector, cancellationToken, ref hitsReturned, ref stopReason);
            return;
        }

        var text = predecodedText ?? Encoding.UTF8.GetString(contentBytes);
        if (predecodedText is null)
        {
            decodedChars += text.Length;
        }
        var comparison = resolvedCaseMode == CaseMode.Sensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var searchIndex = 0;
        var offsetCursor = new Utf8OffsetCursor();

        while (searchIndex <= text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hitsReturned >= hitLimit)
            {
                return;
            }

            var found = text.IndexOf(pattern, searchIndex, comparison);
            if (found < 0)
            {
                return;
            }

            var end = found + pattern.Length;
            if (query.WholeWord && !WordBoundary.IsWholeWord(text, found, end))
            {
                searchIndex = found + 1;
                continue;
            }

            if (query.WholeLine && !IsWholeLine(text, found, end))
            {
                searchIndex = found + 1;
                continue;
            }

            var startByte = offsetCursor.Map(text, found);
            var endByte = startByte + Encoding.UTF8.GetByteCount(text.AsSpan(found, pattern.Length));
            var startLine = LineMap.GetLineFromByteOffset(encodedLineIndex, startByte);
            var endLine = LineMap.GetLineFromByteOffset(encodedLineIndex, Math.Max(startByte, endByte - 1));
            var shouldContinue = collector.OnHit(
                new RawHit(segment.SegmentId, document.Ordinal, startByte, endByte, startLine, endLine));
            hitsReturned++;
            if (!shouldContinue)
            {
                stopReason = SearchStopReason.CollectorStopped;
                return;
            }

            if (hitsReturned >= hitLimit)
            {
                return;
            }

            searchIndex = found + 1;
        }
    }

    private static void SearchAsciiLiteralDocument(MappedSegment segment, SegmentDocument document, ReadOnlySpan<byte> encodedLineIndex, ReadOnlySpan<byte> contentBytes, ReadOnlySpan<byte> needle, int[] prefixTable, bool wholeWord, bool wholeLine, bool caseSensitive, int hitLimit, IHitCollector collector, CancellationToken cancellationToken, ref int hitsReturned, ref SearchStopReason stopReason)
    {
        var offset = 0;
        while (offset <= contentBytes.Length - needle.Length)
        {
            if (hitsReturned >= hitLimit)
            {
                return;
            }

            var relativeIndex = caseSensitive
                ? contentBytes[offset..].IndexOf(needle)
                : IndexOfAsciiInsensitive(contentBytes[offset..], needle, prefixTable, cancellationToken);
            if (relativeIndex < 0)
            {
                return;
            }

            var start = offset + relativeIndex;
            var end = start + needle.Length;
            if (wholeWord && !IsWholeWordAscii(contentBytes, start, end))
            {
                offset = start + 1;
                continue;
            }

            if (wholeLine && !IsWholeLine(contentBytes, start, end))
            {
                offset = start + 1;
                continue;
            }

            var startLine = LineMap.GetLineFromByteOffset(encodedLineIndex, start);
            var endLine = LineMap.GetLineFromByteOffset(encodedLineIndex, Math.Max(start, end - 1));
            var shouldContinue = collector.OnHit(
                new RawHit(segment.SegmentId, document.Ordinal, start, end, startLine, endLine));
            hitsReturned++;
            if (!shouldContinue)
            {
                stopReason = SearchStopReason.CollectorStopped;
                return;
            }

            if (hitsReturned >= hitLimit)
            {
                return;
            }

            offset = start + 1;
        }
    }

    private static bool PassesMandatoryPrecheck(ReadOnlySpan<byte> contentBytes, string[] mandatoryFragments, PreparedMandatoryFragment[] preparedMandatoryFragments, CaseMode resolvedCaseMode, CancellationToken cancellationToken, ref long decodedChars, out string? predecodedText)
    {
        predecodedText = null;
        if (mandatoryFragments.Length == 0)
        {
            return true;
        }

        if (resolvedCaseMode == CaseMode.Sensitive)
        {
            foreach (var fragment in preparedMandatoryFragments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (contentBytes.IndexOf(fragment.Utf8Bytes) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        var requiresUnicodeDecode = false;
        for (var i = 0; i < preparedMandatoryFragments.Length; i++)
        {
            if (!preparedMandatoryFragments[i].IsAscii)
            {
                requiresUnicodeDecode = true;
                break;
            }
        }

        if (requiresUnicodeDecode)
        {
            predecodedText = Encoding.UTF8.GetString(contentBytes);
            decodedChars += predecodedText.Length;
            foreach (var fragment in mandatoryFragments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (predecodedText.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var fragment in preparedMandatoryFragments)
        {
            if (IndexOfAsciiInsensitive(
                    contentBytes,
                    fragment.LoweredAsciiBytes,
                    fragment.AsciiPrefixTable,
                    cancellationToken) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool PassesOrderedAnchorPrecheck(ReadOnlySpan<byte> contentBytes, PreparedOrderedAnchor[][] preparedOrderedAnchorGroups, CancellationToken cancellationToken)
    {
        if (preparedOrderedAnchorGroups.Length == 0)
        {
            return true;
        }

        var maximumGroupLength = preparedOrderedAnchorGroups.Max(static group => group.Length);
        Span<int> nextStartsBuffer = stackalloc int[maximumGroupLength];
        Span<int> latestReachableEndsBuffer = stackalloc int[maximumGroupLength];
        for (var groupIndex = 0; groupIndex < preparedOrderedAnchorGroups.Length; groupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = preparedOrderedAnchorGroups[groupIndex];
            var nextStarts = nextStartsBuffer[..group.Length];
            var latestReachableEnds = latestReachableEndsBuffer[..group.Length];
            latestReachableEnds.Fill(-1);
            // A later reachable prefix end is never worse for an upper-gap or same-line constraint.
            for (var anchorIndex = 0; anchorIndex < group.Length; anchorIndex++)
            {
                var anchor = group[anchorIndex];
                nextStarts[anchorIndex] = FindOrderedAnchor(contentBytes, anchor, searchStart: 0, cancellationToken);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextStart = int.MaxValue;
                for (var anchorIndex = 0; anchorIndex < group.Length; anchorIndex++)
                {
                    if (nextStarts[anchorIndex] >= 0)
                    {
                        nextStart = Math.Min(nextStart, nextStarts[anchorIndex]);
                    }
                }

                if (nextStart == int.MaxValue)
                {
                    break;
                }

                // Process downstream anchors first so equal-position occurrences
                // observe the preceding anchor state from before this event.
                for (var anchorIndex = group.Length - 1; anchorIndex >= 0; anchorIndex--)
                {
                    if (nextStarts[anchorIndex] != nextStart)
                    {
                        continue;
                    }

                    var anchor = group[anchorIndex];
                    var reachable = anchorIndex == 0;
                    if (anchorIndex > 0)
                    {
                        var previousEnd = latestReachableEnds[anchorIndex - 1];
                        reachable = previousEnd >= 0 && previousEnd <= nextStart;
                        if (reachable && anchor.MaxGapBytesFromPrevious is { } maxGap)
                        {
                            reachable = nextStart - previousEnd <= maxGap;
                        }

                        if (reachable && anchor.SameLineFromPrevious)
                        {
                            reachable = IndexOfLineBreak(contentBytes[previousEnd..nextStart], cancellationToken) < 0;
                        }
                    }

                    if (reachable)
                    {
                        latestReachableEnds[anchorIndex] = nextStart + anchor.Utf8Bytes.Length;
                        if (anchorIndex == group.Length - 1)
                        {
                            return true;
                        }
                    }

                    nextStarts[anchorIndex] = FindOrderedAnchor(
                        contentBytes,
                        anchor,
                        searchStart: nextStart + 1,
                        cancellationToken);
                }
            }
        }

        return false;
    }

    private static int FindOrderedAnchor(
        ReadOnlySpan<byte> contentBytes,
        PreparedOrderedAnchor anchor,
        int searchStart,
        CancellationToken cancellationToken)
    {
        if (searchStart > contentBytes.Length - anchor.Utf8Bytes.Length)
        {
            return -1;
        }

        var relative = anchor.LoweredAsciiBytes is null
            ? contentBytes[searchStart..].IndexOf(anchor.Utf8Bytes)
            : IndexOfAsciiInsensitive(contentBytes[searchStart..], anchor.LoweredAsciiBytes, anchor.AsciiPrefixTable, cancellationToken);
        return relative < 0 ? -1 : searchStart + relative;
    }

    private static bool ShouldUseOrderedAnchorPrecheck(PreparedOrderedAnchor[][] preparedOrderedAnchorGroups, int candidateCount)
    {
        if (preparedOrderedAnchorGroups.Length == 0)
        {
            return false;
        }

        var candidateLimit = HasOnlyConstrainedOrderedAnchorGroups(preparedOrderedAnchorGroups)
            ? ConstrainedOrderedAnchorPrecheckCandidateLimit
            : OrderedAnchorPrecheckCandidateLimit;
        return candidateCount <= candidateLimit;
    }

    private static bool HasOnlyConstrainedOrderedAnchorGroups(PreparedOrderedAnchor[][] preparedOrderedAnchorGroups)
    {
        for (var groupIndex = 0; groupIndex < preparedOrderedAnchorGroups.Length; groupIndex++)
        {
            var group = preparedOrderedAnchorGroups[groupIndex];
            if (group.Length < 2)
            {
                return false;
            }

            var hasConstraint = false;
            for (var anchorIndex = 1; anchorIndex < group.Length; anchorIndex++)
            {
                var anchor = group[anchorIndex];
                if (anchor.SameLineFromPrevious || anchor.MaxGapBytesFromPrevious is <= MaxConstrainedGapSearchBytes)
                {
                    hasConstraint = true;
                    continue;
                }

                return false;
            }

            if (!hasConstraint)
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOfLineBreak(ReadOnlySpan<byte> haystack, CancellationToken cancellationToken)
    {
        for (var i = 0; i < haystack.Length; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (haystack[i] is (byte)'\r' or (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfAsciiInsensitive(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> loweredNeedle, ReadOnlySpan<int> prefixTable, CancellationToken cancellationToken)
    {
        if (loweredNeedle.Length == 0)
        {
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var matched = 0;
        for (var i = 0; i < haystack.Length; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var current = AsciiCase.Fold(haystack[i]);
            while (matched > 0 && current != loweredNeedle[matched])
            {
                matched = prefixTable[matched - 1];
            }

            if (current == loweredNeedle[matched])
            {
                matched++;
                if (matched == loweredNeedle.Length)
                {
                    return i - loweredNeedle.Length + 1;
                }
            }
        }

        return -1;
    }

    private static int[] BuildPrefixTable(ReadOnlySpan<byte> needle)
    {
        var prefixTable = new int[needle.Length];
        var matched = 0;
        for (var i = 1; i < needle.Length; i++)
        {
            while (matched > 0 && needle[i] != needle[matched])
            {
                matched = prefixTable[matched - 1];
            }

            if (needle[i] == needle[matched])
            {
                matched++;
            }

            prefixTable[i] = matched;
        }

        return prefixTable;
    }

    private static byte[] LowerAscii(ReadOnlySpan<byte> needle)
    {
        var loweredNeedle = GC.AllocateUninitializedArray<byte>(needle.Length);
        for (var i = 0; i < needle.Length; i++)
        {
            loweredNeedle[i] = AsciiCase.Fold(needle[i]);
        }

        return loweredNeedle;
    }

    private static PreparedSearchQuery PrepareSearchQuery(SearchQuery query)
    {
        var key = new PreparedQueryKey(
            query.Pattern,
            query.PatternMode,
            query.CaseMode,
            query.WholeWord,
            query.WholeLine,
            query.Multiline,
            query.DotAll,
            query.TimeoutPolicy);
        return PreparedQueryCache.GetOrAdd(
            key,
            static cacheKey => CreatePreparedSearchQuery(cacheKey),
            out _);
    }

    private static PreparedSearchQuery CreatePreparedSearchQuery(PreparedQueryKey key)
    {
        var query = key.ToSearchQuery();
        var resolvedCaseMode = ResolveCaseMode(query);
        Utf8RegexSyntax? utf8Syntax = null;
        RegexFragmentAnalyzer.PatternAnalysis analysis = default;
        if (query.PatternMode == PatternMode.Regex)
        {
            if (Utf8RegexEngine.TryParseSyntax(query, resolvedCaseMode, out var parsedSyntax))
            {
                utf8Syntax = parsedSyntax;
                analysis = RegexFragmentAnalyzer.AnalyzePattern(parsedSyntax);
            }
            else
            {
                analysis = RegexFragmentAnalyzer.AnalyzePattern(query.Pattern);
            }
        }

        var regexPlan = RegexExecutionPlanner.Resolve(query, resolvedCaseMode, analysis, utf8Syntax);
        var anchors = BuildQueryAnchors(query, resolvedCaseMode, regexPlan, analysis);
        return new PreparedSearchQuery(
            resolvedCaseMode,
            regexPlan,
            anchors,
            anchors.GetMandatoryTexts(),
            anchors.GetAdvisoryTexts(),
            PrepareLiteralQuery(query, resolvedCaseMode, regexPlan.ExactLiteral),
            PrepareMandatoryFragments(anchors.MandatoryAnchors, resolvedCaseMode),
            PrepareOrderedAnchorGroups(anchors.OrderedAnchorGroups),
            () => PrepareRegexExecution(query, resolvedCaseMode, regexPlan));
    }

    private static PreparedLiteralQuery? PrepareLiteralQuery(
        SearchQuery query,
        CaseMode resolvedCaseMode,
        string? literalOverride)
    {
        var pattern = literalOverride ?? query.Pattern;
        if ((query.PatternMode != PatternMode.Literal && literalOverride is null) || string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        var needle = Encoding.UTF8.GetBytes(pattern);
        var isAscii = needle.AsSpan().IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0;
        var loweredAsciiNeedle = resolvedCaseMode == CaseMode.Insensitive && isAscii
            ? LowerAscii(needle)
            : null;
        return new PreparedLiteralQuery(
            needle,
            loweredAsciiNeedle,
            loweredAsciiNeedle is null ? null : BuildPrefixTable(loweredAsciiNeedle),
            isAscii);
    }

    private static PreparedMandatoryFragment[] PrepareMandatoryFragments(
        SearchLiteralAnchor[] mandatoryAnchors,
        CaseMode resolvedCaseMode)
    {
        if (mandatoryAnchors.Length == 0)
        {
            return [];
        }

        var prepared = new PreparedMandatoryFragment[mandatoryAnchors.Length];
        for (var i = 0; i < mandatoryAnchors.Length; i++)
        {
            var text = mandatoryAnchors[i].Text;
            var utf8Bytes = Encoding.UTF8.GetBytes(text);
            var isAscii = utf8Bytes.AsSpan().IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0;
            var loweredAsciiBytes = resolvedCaseMode == CaseMode.Insensitive && isAscii
                ? LowerAscii(utf8Bytes)
                : [];
            prepared[i] = new PreparedMandatoryFragment(
                text,
                utf8Bytes,
                loweredAsciiBytes,
                BuildPrefixTable(loweredAsciiBytes),
                isAscii);
        }

        return prepared;
    }

    private static PreparedOrderedAnchor[][] PrepareOrderedAnchorGroups(SearchOrderedAnchorGroup[] groups)
    {
        if (groups.Length == 0)
        {
            return [];
        }

        var prepared = new PreparedOrderedAnchor[groups.Length][];
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex].Anchors;
            prepared[groupIndex] = new PreparedOrderedAnchor[group.Length];
            for (var anchorIndex = 0; anchorIndex < group.Length; anchorIndex++)
            {
                var anchor = group[anchorIndex];
                var lowered = anchor.IgnoreCase ? LowerAscii(anchor.Utf8Bytes) : null;
                prepared[groupIndex][anchorIndex] = new PreparedOrderedAnchor(
                    anchor.Utf8Bytes,
                    lowered,
                    lowered is null ? [] : BuildPrefixTable(lowered),
                    anchor.MaxGapBytesFromPrevious,
                    anchor.SameLineFromPrevious);
            }
        }

        return prepared;
    }

    private static PreparedRegexExecution PrepareRegexExecution(
        SearchQuery query,
        CaseMode resolvedCaseMode,
        in RegexExecutionPlan regexPlan)
    {
        if (query.PatternMode != PatternMode.Regex || regexPlan.IsExactLiteral)
        {
            return default;
        }

        if (regexPlan.Utf8RegexProgram is { } utf8RegexProgram)
        {
            return new PreparedRegexExecution(
                Utf8RegexEngine.Prepare(utf8RegexProgram, query.TimeoutPolicy),
                RegexVerifier.Prepare(query, resolvedCaseMode));
        }

        return regexPlan.PreparedFallbackRegex is { } fallbackRegex
            ? new PreparedRegexExecution(null, fallbackRegex)
            : throw new InvalidOperationException("The regex execution plan has no fallback verifier.");
    }

    private static SearchQueryAnchors BuildQueryAnchors(
        SearchQuery query,
        CaseMode resolvedCaseMode,
        in RegexExecutionPlan regexPlan,
        in RegexFragmentAnalyzer.PatternAnalysis analysis)
    {
        if (query.PatternMode == PatternMode.Regex &&
            resolvedCaseMode == CaseMode.Insensitive &&
            !regexPlan.UsesUtf8Regex)
        {
            // A mandatory byte fragment is not mandatory under invariant Unicode
            // regex folding unless the pattern has byte-safe semantics for ASCII documents.
            return new SearchQueryAnchors([], [], CaseMode.Sensitive, []);
        }

        var mandatoryFragments = GetMandatoryFragments(query, regexPlan.ExactLiteral, analysis);
        var advisorySeeds = GetAdvisorySeeds(query, regexPlan.ExactLiteral, mandatoryFragments, analysis, out var advisoryCaseMode);
        var mandatoryAnchors = new SearchLiteralAnchor[mandatoryFragments.Length];
        for (var i = 0; i < mandatoryFragments.Length; i++)
        {
            mandatoryAnchors[i] = new SearchLiteralAnchor(mandatoryFragments[i]);
        }

        var advisoryAnchors = new SearchLiteralAnchor[advisorySeeds.Length];
        for (var i = 0; i < advisorySeeds.Length; i++)
        {
            advisoryAnchors[i] = new SearchLiteralAnchor(advisorySeeds[i]);
        }

        var utf8RegexProgram = regexPlan.Utf8RegexProgram;
        var regexGroups = utf8RegexProgram?.OrderedAnchorGroups;
        if (utf8RegexProgram is null || regexGroups is null || regexGroups.Length == 0)
        {
            return new SearchQueryAnchors(mandatoryAnchors, advisoryAnchors, advisoryCaseMode, []);
        }

        var orderedAnchorGroups = new SearchOrderedAnchorGroup[regexGroups.Length];
        var ignoreCase = utf8RegexProgram.IgnoreCase;
        for (var groupIndex = 0; groupIndex < regexGroups.Length; groupIndex++)
        {
            var regexAnchors = regexGroups[groupIndex].Anchors;
            var anchors = new SearchOrderedAnchor[regexAnchors.Length];
            for (var anchorIndex = 0; anchorIndex < regexAnchors.Length; anchorIndex++)
            {
                var anchor = regexAnchors[anchorIndex];
                anchors[anchorIndex] = new SearchOrderedAnchor(anchor.Utf8Bytes, ignoreCase, anchor.MaxGapBytesFromPrevious, anchor.SameLineFromPrevious);
            }

            orderedAnchorGroups[groupIndex] = new SearchOrderedAnchorGroup(anchors);
        }

        return new SearchQueryAnchors(mandatoryAnchors, advisoryAnchors, advisoryCaseMode, orderedAnchorGroups);
    }

    private static string[] GetMandatoryFragments(
        SearchQuery query,
        string? exactRegexLiteral,
        in RegexFragmentAnalyzer.PatternAnalysis analysis)
    {
        if (query.PatternMode == PatternMode.Regex)
        {
            return exactRegexLiteral is not null
                ? [exactRegexLiteral]
                : PruneRedundantMandatoryFragments(analysis.MandatoryFragments);
        }

        return string.IsNullOrEmpty(query.Pattern) ? [] : [query.Pattern];
    }

    private static string[] GetAdvisorySeeds(
        SearchQuery query,
        string? exactRegexLiteral,
        string[] mandatoryFragments,
        in RegexFragmentAnalyzer.PatternAnalysis analysis,
        out CaseMode advisoryCaseMode)
    {
        advisoryCaseMode = query.CaseMode switch
        {
            CaseMode.Insensitive => CaseMode.Insensitive,
            _ => CaseMode.Sensitive
        };

        if (query.PatternMode != PatternMode.Regex || exactRegexLiteral is not null)
        {
            return [];
        }

        var advisoryPlan = analysis.AdvisorySeeds;
        advisoryCaseMode = advisoryPlan.RequiresIgnoreCase ? CaseMode.Insensitive : advisoryCaseMode;
        var seeds = advisoryPlan.Seeds;
        if (seeds.Length == 0)
        {
            return [];
        }

        var filtered = new List<string>(seeds.Length);
        for (var i = 0; i < seeds.Length; i++)
        {
            var seed = seeds[i];
            if (seed.Length < 3)
            {
                continue;
            }

            var redundant = false;
            for (var j = 0; j < mandatoryFragments.Length; j++)
            {
                if (mandatoryFragments[j].Contains(seed, StringComparison.Ordinal))
                {
                    redundant = true;
                    break;
                }
            }

            if (!redundant)
            {
                filtered.Add(seed);
            }
        }

        return [.. filtered];
    }

    private static string[] PruneRedundantMandatoryFragments(string[] fragments)
    {
        if (fragments.Length <= 1)
        {
            return fragments;
        }

        var ordered = fragments
            .Where(static fragment => fragment.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static fragment => fragment.Length)
            .ThenBy(static fragment => fragment, StringComparer.Ordinal)
            .ToArray();
        var selected = new List<string>(ordered.Length);
        foreach (var fragment in ordered)
        {
            var redundant = false;
            for (var i = 0; i < selected.Count; i++)
            {
                if (selected[i].Contains(fragment, StringComparison.Ordinal))
                {
                    redundant = true;
                    break;
                }
            }

            if (!redundant)
            {
                selected.Add(fragment);
            }
        }

        return [.. selected];
    }

    private static CaseMode ResolveCaseMode(SearchQuery query)
    {
        return query.CaseMode switch
        {
            CaseMode.Sensitive => CaseMode.Sensitive,
            CaseMode.Insensitive => CaseMode.Insensitive,
            CaseMode.Smart => Hashing.ContainsUppercase(query.Pattern) ? CaseMode.Sensitive : CaseMode.Insensitive,
            _ => CaseMode.Sensitive
        };
    }

    private static bool IsWholeWordAscii(ReadOnlySpan<byte> contentBytes, int start, int end)
    {
        return IsBoundaryAscii(contentBytes, start - 1) && IsBoundaryAscii(contentBytes, end);
    }

    private static bool IsBoundaryAscii(ReadOnlySpan<byte> contentBytes, int index)
    {
        if (index < 0 || index >= contentBytes.Length)
        {
            return true;
        }

        var value = contentBytes[index];
        return !(value is >= (byte)'A' and <= (byte)'Z' ||
                 value is >= (byte)'a' and <= (byte)'z' ||
                 value is >= (byte)'0' and <= (byte)'9' ||
                 value == (byte)'_');
    }

    private static bool IsWholeLine(string text, int start, int end)
    {
        var lineStart = start;
        while (lineStart > 0 && text[lineStart - 1] != '\n')
        {
            lineStart--;
        }

        var lineEnd = end;
        while (lineEnd < text.Length && text[lineEnd] != '\n')
        {
            lineEnd++;
        }

        var match = text[start..end];
        var line = text[lineStart..lineEnd].TrimEnd('\r');
        return string.Equals(line, match, StringComparison.Ordinal);
    }

    private static bool IsWholeLine(ReadOnlySpan<byte> contentBytes, int start, int end)
    {
        var lineStart = start;
        while (lineStart > 0 && contentBytes[lineStart - 1] != (byte)'\n')
        {
            lineStart--;
        }

        var lineEnd = end;
        while (lineEnd < contentBytes.Length && contentBytes[lineEnd] != (byte)'\n')
        {
            lineEnd++;
        }

        var trimmedEnd = lineEnd > lineStart && contentBytes[lineEnd - 1] == (byte)'\r'
            ? lineEnd - 1
            : lineEnd;
        return start == lineStart && end == trimmedEnd;
    }

    private static bool CanUseUtf8RegexForDocument(SegmentDocument document)
    {
        return (document.Flags & (1 << 1)) != 0;
    }

    private static bool ShouldIncludeNonAsciiRegexCandidates(
        SearchQuery query,
        CaseMode resolvedCaseMode,
        in RegexExecutionPlan regexPlan,
        in SearchPlanCandidate plan)
    {
        return query.PatternMode == PatternMode.Regex &&
            resolvedCaseMode == CaseMode.Insensitive &&
            regexPlan.UsesUtf8Regex &&
            plan.CandidatePlan.UsesIndex;
    }

    private static int GetUtf16Column(ReadOnlySpan<byte> contentBytes, ReadOnlySpan<byte> encodedLineIndex, int byteOffset, int oneBasedLine, bool asciiDocument)
    {
        var start = LineMap.GetLineStart(encodedLineIndex, oneBasedLine);
        if (asciiDocument)
        {
            return Math.Min(byteOffset, contentBytes.Length) - start + 1;
        }

        return Encoding.UTF8.GetCharCount(contentBytes[start..Math.Min(byteOffset, contentBytes.Length)]) + 1;
    }

    private static string[] CollectContext(
        ReadOnlySpan<byte> contentBytes,
        ReadOnlySpan<byte> encodedLineIndex,
        int startLine,
        int endLine,
        bool asciiDocument,
        ref int remainingLines,
        ref int remainingBytes,
        out bool truncated)
    {
        truncated = false;
        var lineCount = LineMap.GetLineCount(encodedLineIndex);
        if (lineCount == 0)
        {
            return [];
        }

        startLine = Math.Max(startLine, 1);
        endLine = Math.Min(endLine, lineCount);
        if (startLine > endLine)
        {
            return [];
        }

        var requestedLines = endLine - startLine + 1;
        var lines = new List<string>(Math.Min(requestedLines, remainingLines));
        for (var line = startLine; line <= endLine; line++)
        {
            if (remainingLines == 0 || remainingBytes == 0)
            {
                truncated = true;
                break;
            }

            var start = LineMap.GetLineStart(encodedLineIndex, line);
            var end = LineMap.GetLineEndExclusive(encodedLineIndex, line, contentBytes.Length);
            var materializedEnd = Math.Min(end, start + remainingBytes);
            if (materializedEnd < end && !asciiDocument)
            {
                while (materializedEnd > start &&
                       (contentBytes[materializedEnd] & 0xC0) == 0x80)
                {
                    materializedEnd--;
                }
            }

            lines.Add(DecodeText(contentBytes[start..materializedEnd], asciiDocument, trimLineEnd: true));
            remainingLines--;
            remainingBytes -= materializedEnd - start;
            if (materializedEnd < end)
            {
                truncated = true;
                break;
            }
        }

        return [.. lines];
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes, bool asciiDocument, bool trimLineEnd)
    {
        if (trimLineEnd)
        {
            while (bytes.Length > 0 && bytes[^1] is (byte)'\r' or (byte)'\n')
            {
                bytes = bytes[..^1];
            }
        }

        return asciiDocument
            ? Encoding.ASCII.GetString(bytes)
            : Encoding.UTF8.GetString(bytes);
    }

    private bool CanUseSegmentParallelSearch(SearchQuery query)
    {
        return _searchOptions.SegmentParallelism > 1 &&
               _segments.Count > 1 &&
               query.MaxHits == int.MaxValue &&
               query.MaxDocuments == int.MaxValue &&
               query.MaxBytesScanned == long.MaxValue;
    }
}

internal readonly record struct SegmentScope(
    OrdinalSet Ordinals,
    long Bytes,
    int NonAsciiDocuments,
    long NonAsciiBytes)
{
    public int Count => Ordinals.Count;
}

internal interface IPathVisibility
{
    bool IsVisible(ReadOnlySpan<byte> normalizedPathUtf8);
}

internal readonly struct SegmentCache
{
    public SegmentCache(
        MappedSegment segment,
        uint[] deadOrdinals,
        long liveContentBytes,
        int liveNonAsciiDocuments,
        long liveNonAsciiBytes)
    {
        Segment = segment;
        DeadOrdinals = deadOrdinals;
        LiveContentBytes = liveContentBytes;
        LiveNonAsciiDocuments = liveNonAsciiDocuments;
        LiveNonAsciiBytes = liveNonAsciiBytes;
    }

    public MappedSegment Segment { get; }
    public uint[] DeadOrdinals { get; }

    public long LiveContentBytes { get; }
    public int LiveNonAsciiDocuments { get; }
    public long LiveNonAsciiBytes { get; }
}

internal readonly struct SnapshotOperation : IDisposable
{
    private readonly ReaderWriterLockSlim _lifetimeLock;

    public SnapshotOperation(ReaderWriterLockSlim lifetimeLock)
    {
        _lifetimeLock = lifetimeLock;
    }

    public void Dispose()
    {
        _lifetimeLock.ExitReadLock();
    }
}

internal readonly record struct SegmentSearchResult(
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
    SearchStopReason StopReason);

internal struct PlannerAlternativeAccumulator
{
    public double EstimatedDirectScanCost;
    public double? EstimatedIndexedCost;
    public double EstimatedCandidateDocuments;
    public double EstimatedCost;
    public int SegmentCount;
    public int ScopeDocumentCount;
    public long ScopeByteCount;

    public void Add(in SearchPlanCandidate plan, in SegmentScope scope)
    {
        EstimatedDirectScanCost += plan.EstimatedDirectScanCost;
        if (plan.EstimatedIndexedCost is double estimatedIndexedCost)
        {
            EstimatedIndexedCost = (EstimatedIndexedCost ?? 0d) + estimatedIndexedCost;
        }

        EstimatedCandidateDocuments += plan.EstimatedCandidateDocuments;
        EstimatedCost += plan.EstimatedCost;
        SegmentCount++;
        ScopeDocumentCount += scope.Count;
        ScopeByteCount += scope.Bytes;
    }

    public void Add(PlannerAlternative alternative)
    {
        EstimatedDirectScanCost += alternative.EstimatedDirectScanCost;
        if (alternative.EstimatedIndexedCost is double estimatedIndexedCost)
        {
            EstimatedIndexedCost = (EstimatedIndexedCost ?? 0d) + estimatedIndexedCost;
        }

        EstimatedCandidateDocuments += alternative.EstimatedCandidateDocuments;
        EstimatedCost += alternative.EstimatedCost;
        SegmentCount += alternative.SegmentCount;
        ScopeDocumentCount += alternative.ScopeDocumentCount;
        ScopeByteCount += alternative.ScopeByteCount;
    }

    public PlannerAlternative ToPlannerAlternative(
        SearchCandidateKind candidateKind,
        SearchCandidateCombination candidateCombination,
        SearchVerifierKind verifierKind,
        bool usesIndex)
    {
        return new PlannerAlternative(
            candidateKind,
            candidateCombination,
            verifierKind,
            usesIndex,
            EstimatedDirectScanCost,
            EstimatedIndexedCost,
            EstimatedCandidateDocuments,
            EstimatedCost,
            SegmentCount,
            ScopeDocumentCount,
            ScopeByteCount);
    }
}

