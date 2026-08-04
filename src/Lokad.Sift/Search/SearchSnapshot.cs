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
    private const int OrderedAnchorPrecheckCandidateLimit = 3000;
    private const int ConstrainedOrderedAnchorPrecheckCandidateLimit = 50000;
    private const int MaxConstrainedGapSearchBytes = 512;
    private const int ParallelHitBufferCapacity = 256;
    private readonly IReadOnlyList<SegmentManifestEntry> _segments;
    private readonly ISiftStorage _storage;
    private readonly SiftOptions _options;
    private readonly SearchOptions _searchOptions;
    private readonly StorageReadSnapshot _storageSnapshot;
    private readonly SearchPlanExplainer _planExplainer;
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
    internal IndexIdentity Identity => new(
        _storageSnapshot.Manifest.CorpusId,
        _storageSnapshot.Manifest.ContentRevision,
        _storageSnapshot.Manifest.Generation);

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
        _planExplainer = new SearchPlanExplainer(_segments, _options, OpenSegment);
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
            using var operation = BeginOperation();
            return _planExplainer.Explain(query, pathFilter, SearchQueryPreparer.Prepare(query));
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            ReportIndexFailure(exception);
            throw;
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
            using var operation = BeginOperation();
            var watch = Stopwatch.StartNew();
            var preparedQuery = SearchQueryPreparer.Prepare(query);
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
                    guardedCollector,
                    pathVisibility,
                    effectiveCancellationToken,
                    cancellationToken,
                    watch)
                : SearchSequential(
                    query,
                    matcher,
                    preparedQuery,
                    guardedCollector,
                    pathVisibility,
                    effectiveCancellationToken,
                    cancellationToken,
                    watch);
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
        PreparedRegexVerifier? fallbackRegex = null;
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
                        else if ((fallbackRegex ??= preparedRegex.GetFallbackRegex()) is { } preparedFallbackRegex)
                        {
                            RegexVerifier.TrySearchDocument(cache.Segment, document, preparedFallbackRegex, documentHitLimit, collector, ref decodedChars, ref hitsReturned, ref fallbackRegexInvocations, ref nonBacktrackingRegexInvocations, ref stopReason, predecodedText);
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

                    if (hitDocuments >= query.MaxMatchedDocuments)
                    {
                        stopReason = SearchStopReason.MaxMatchedDocuments;
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
            using var operation = BeginOperation();
            var cache = OpenSegment(hit.SegmentId);
            return HitMaterializer.Materialize(
                cache.Segment,
                hit,
                contextBefore,
                contextAfter,
                _options);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            ReportIndexFailure(exception);
            throw;
        }
    }

    public bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content)
    {
        try
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
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            ReportIndexFailure(exception);
            throw;
        }
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
            using var operation = BeginOperation();
            var cache = OpenSegment(hit.SegmentId);
            var document = cache.Segment.GetDocument(hit.DocumentOrdinal);
            return cache.Segment.GetPath(document);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            ReportIndexFailure(exception);
            throw;
        }
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

        if (query.MaxMatchedDocuments <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "MaxMatchedDocuments must be positive.");
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
        PreparedRegexVerifier? fallbackRegex = null;
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

                if (segmentHitDocuments >= query.MaxMatchedDocuments)
                {
                    segmentStopReason = SearchStopReason.MaxMatchedDocuments;
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
            else if ((fallbackRegex ??= preparedRegex.GetFallbackRegex()) is { } preparedFallbackRegex)
            {
                RegexVerifier.TrySearchDocument(cache.Segment, document, preparedFallbackRegex, documentHitLimit, collector, ref decodedChars, ref hitsReturned, ref fallbackRegexInvocations, ref nonBacktrackingRegexInvocations, ref documentStopReason, predecodedText);
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

            if (segmentHitDocuments >= query.MaxMatchedDocuments)
            {
                segmentStopReason = SearchStopReason.MaxMatchedDocuments;
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
            var loweredNeedle = preparedLiteral?.LoweredAsciiNeedle ?? SearchQueryPreparer.LowerAscii(needle);
            var prefixTable = preparedLiteral?.AsciiPrefixTable ?? SearchQueryPreparer.BuildPrefixTable(loweredNeedle);
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

    private bool CanUseSegmentParallelSearch(SearchQuery query)
    {
        return _searchOptions.SegmentParallelism > 1 &&
               _segments.Count > 1 &&
               query.MaxHits == int.MaxValue &&
               query.MaxMatchedDocuments == int.MaxValue &&
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

