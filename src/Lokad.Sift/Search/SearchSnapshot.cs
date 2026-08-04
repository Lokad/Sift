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
        new CorpusId(_storageSnapshot.Manifest.CorpusId),
        new ContentRevision(_storageSnapshot.Manifest.ContentRevision),
        new StorageGeneration(_storageSnapshot.Manifest.Generation));

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
        var preparedRegex = preparedQuery.GetRegexExecution();
        var metrics = SearchMetrics.ForRegex(preparedRegex);
        var hitsReturned = 0;
        var matchedDocuments = 0;
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

                var result = ExecuteSegmentSearch(
                    OpenSegment(entry, cancellationToken),
                    query,
                    matcher,
                    preparedQuery,
                    preparedRegex,
                    pathVisibility,
                    watch,
                    collector,
                    new SearchLimits(
                        query.MaxHits - hitsReturned,
                        query.MaxMatchedDocuments - matchedDocuments,
                        query.MaxBytesScanned - metrics.BytesScanned),
                    cancellationToken,
                    cancellationToken,
                    callerCancellationToken);
                metrics.Add(result.Metrics);
                hitsReturned += result.HitsReturned;
                matchedDocuments += result.MatchedDocuments;
                if (result.StopReason != SearchStopReason.None)
                {
                    stopReason = result.StopReason;
                    break;
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
        return metrics.ToSearchStats(watch.ElapsedMilliseconds, hitsReturned, stopReason);
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
        var preparedRegex = preparedQuery.GetRegexExecution();
        var metrics = SearchMetrics.ForRegex(preparedRegex);
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
                                batchResults[resultIndex] = ExecuteSegmentSearch(
                                    cache,
                                    query,
                                    matcher,
                                    preparedQuery,
                                    preparedRegex,
                                    pathVisibility,
                                    watch,
                                    bufferedCollector,
                                    SearchLimits.FromQuery(query),
                                    batchCancellation.Token,
                                    cancellationToken,
                                    callerCancellationToken);
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
                            metrics.Add(result.Metrics);

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
        catch (RegexMatchTimeoutException) when (query.TimeoutPolicy != TimeoutPolicy.Throw)
        {
            stopReason = SearchStopReason.RegexTimeout;
        }

    Done:
        watch.Stop();
        return metrics.ToSearchStats(watch.ElapsedMilliseconds, hitsReturned, stopReason);
    }

    public MaterializedHit Materialize(in RawHit hit, int contextBefore, int contextAfter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contextBefore);
        ArgumentOutOfRangeException.ThrowIfNegative(contextAfter);
        try
        {
            using var operation = BeginOperation();
            var cache = OpenSegment(hit.SegmentId.Value);
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
            var cache = OpenSegment(hit.SegmentId.Value);
            var document = cache.Segment.GetDocument(hit.DocumentOrdinal.Value);
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

    private static int GetDocumentHitLimit(
        int hitsReturned,
        int maximumHits,
        int maximumHitsPerDocument)
    {
        return (int)Math.Min(
            maximumHits,
            Math.Min(int.MaxValue, (long)hitsReturned + maximumHitsPerDocument));
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

    private SegmentSearchResult ExecuteSegmentSearch(
        SegmentCache cache,
        SearchQuery query,
        PathFilterMatcher matcher,
        PreparedSearchQuery preparedQuery,
        PreparedRegexExecutionAccess preparedRegex,
        IPathVisibility? pathVisibility,
        Stopwatch watch,
        IHitCollector collector,
        SearchLimits limits,
        CancellationToken workerCancellationToken,
        CancellationToken searchCancellationToken,
        CancellationToken callerCancellationToken)
    {
        var segmentPlan = SearchPlanner.CreateSegmentPlan(
            cache,
            matcher,
            query,
            preparedQuery.ResolvedCaseMode,
            preparedQuery.Anchors,
            preparedQuery.RegexPlan,
            _options,
            workerCancellationToken);
        var scope = segmentPlan.Scope;
        var candidates = CandidateGenerator.Generate(cache.Segment, scope.Ordinals, segmentPlan.ContentPlan.ChosenPlan.CandidatePlan);
        if (ShouldIncludeNonAsciiRegexCandidates(
            query,
            preparedQuery.ResolvedCaseMode,
            preparedQuery.RegexPlan,
            segmentPlan.ContentPlan.ChosenPlan))
        {
            candidates = CandidateGenerator.IncludeNonAsciiDocuments(cache.Segment, scope.Ordinals, candidates);
        }
        var utf8RegexStartPrecheckGroups = segmentPlan.ContentPlan.ChosenPlan.CandidateKind == SearchCandidateKind.DirectScan
            ? preparedQuery.OrderedAnchorGroups
            : [];
        var useOrderedAnchorPrecheck = ShouldUseOrderedAnchorPrecheck(
            preparedQuery.OrderedAnchorGroups,
            candidates.Ordinals.Count);
        var metrics = new SearchMetrics
        {
            ScopeDocuments = scope.Count,
            ScopeBytes = scope.Bytes,
            PostingsBytesRead = candidates.PostingsBytesRead,
            CandidateDocumentsAfterIndex = candidates.Ordinals.Count
        };
        var segmentStopReason = SearchStopReason.None;
        PreparedRegexVerifier? fallbackRegex = null;
        var hitsReturned = 0;
        var matchedDocuments = 0;

        try
        {
            foreach (var ordinal in candidates.Ordinals)
            {
                if (workerCancellationToken.IsCancellationRequested)
                {
                    searchCancellationToken.ThrowIfCancellationRequested();
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
                        new RawHit(
                            new SegmentId(cache.Segment.SegmentId),
                            new DocumentOrdinal(document.Ordinal),
                            0,
                            0,
                            1,
                            1));
                    metrics.CandidateDocumentsAfterPrecheck++;
                    metrics.VerifiedDocuments++;
                    hitsReturned++;
                    matchedDocuments++;
                    if (!shouldContinue)
                    {
                        segmentStopReason = SearchStopReason.CollectorStopped;
                        break;
                    }

                    if (matchedDocuments >= limits.MaximumMatchedDocuments)
                    {
                        segmentStopReason = SearchStopReason.MaxMatchedDocuments;
                        break;
                    }

                    if (hitsReturned >= limits.MaximumHits)
                    {
                        segmentStopReason = SearchStopReason.MaxHits;
                        break;
                    }

                    continue;
                }

                if (document.ContentLength > limits.MaximumBytesScanned - metrics.BytesScanned)
                {
                    segmentStopReason = SearchStopReason.MaxBytesScanned;
                    break;
                }

                metrics.BytesScanned += document.ContentLength;
                var contentBytes = cache.Segment.GetContentBytes(document);
                var needsUnicodeCaseFoldVerification =
                    query.PatternMode == PatternMode.Regex &&
                    preparedQuery.ResolvedCaseMode == CaseMode.Insensitive &&
                    !CanUseUtf8RegexForDocument(document);
                string? predecodedText = null;
                if (!needsUnicodeCaseFoldVerification &&
                    !PassesMandatoryPrecheck(
                        contentBytes,
                        preparedQuery.MandatoryFragments,
                        preparedQuery.MandatoryFragmentsForPrecheck,
                        preparedQuery.ResolvedCaseMode,
                        searchCancellationToken,
                        ref metrics.DecodedChars,
                        out predecodedText))
                {
                    continue;
                }

                if (!needsUnicodeCaseFoldVerification &&
                    useOrderedAnchorPrecheck &&
                    !PassesOrderedAnchorPrecheck(
                        contentBytes,
                        preparedQuery.OrderedAnchorGroups,
                        searchCancellationToken))
                {
                    continue;
                }

                metrics.CandidateDocumentsAfterPrecheck++;
                metrics.VerifiedDocuments++;
                var countBefore = hitsReturned;
                var documentHitLimit = GetDocumentHitLimit(
                    hitsReturned,
                    limits.MaximumHits,
                    query.MaxHitsPerDocument);
                var documentStopReason = SearchStopReason.None;
                if (query.PatternMode == PatternMode.Literal || preparedQuery.RegexPlan.IsExactLiteral)
                {
                    SearchLiteralDocument(
                        cache.Segment,
                        document,
                        query,
                        preparedQuery.ResolvedCaseMode,
                        preparedQuery.Literal,
                        documentHitLimit,
                        collector,
                        searchCancellationToken,
                        ref metrics.DecodedChars,
                        ref hitsReturned,
                        ref documentStopReason,
                        predecodedText,
                        preparedQuery.RegexPlan.ExactLiteral);
                }
                else if (preparedRegex.Utf8Regex is { } utf8Regex && CanUseUtf8RegexForDocument(document))
                {
                    Utf8RegexEngine.TrySearchDocument(
                        cache.Segment,
                        document,
                        utf8Regex,
                        utf8RegexStartPrecheckGroups,
                        documentHitLimit,
                        collector,
                        ref hitsReturned,
                        ref metrics.Utf8RegexInvocations,
                        ref metrics.Utf8RegexMatchedDocuments,
                        ref metrics.Utf8RegexMatchesEnumerated,
                        ref documentStopReason);
                }
                else if ((fallbackRegex ??= preparedRegex.GetFallbackRegex()) is { } preparedFallbackRegex)
                {
                    RegexVerifier.TrySearchDocument(
                        cache.Segment,
                        document,
                        preparedFallbackRegex,
                        documentHitLimit,
                        collector,
                        ref metrics.DecodedChars,
                        ref hitsReturned,
                        ref metrics.FallbackRegexInvocations,
                        ref metrics.NonBacktrackingRegexInvocations,
                        ref documentStopReason,
                        predecodedText);
                }
                else
                {
                    throw new InvalidOperationException("The regex execution plan has no prepared verifier.");
                }

                if (documentStopReason == SearchStopReason.None && hitsReturned >= limits.MaximumHits)
                {
                    documentStopReason = SearchStopReason.MaxHits;
                }

                if (hitsReturned > countBefore)
                {
                    matchedDocuments++;
                }

                if (documentStopReason != SearchStopReason.None)
                {
                    segmentStopReason = documentStopReason;
                    break;
                }

                if (matchedDocuments >= limits.MaximumMatchedDocuments)
                {
                    segmentStopReason = SearchStopReason.MaxMatchedDocuments;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (
            !callerCancellationToken.IsCancellationRequested &&
            searchCancellationToken.IsCancellationRequested)
        {
            segmentStopReason = SearchStopReason.MaxElapsedTime;
        }

        metrics.RegexInvocations = metrics.Utf8RegexInvocations + metrics.FallbackRegexInvocations;
        return new SegmentSearchResult(
            metrics,
            hitsReturned,
            matchedDocuments,
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
            var shouldContinue = collector.OnHit(new RawHit(
                new SegmentId(segment.SegmentId),
                new DocumentOrdinal(document.Ordinal),
                0,
                0,
                1,
                1));
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
                new RawHit(
                    new SegmentId(segment.SegmentId),
                    new DocumentOrdinal(document.Ordinal),
                    startByte,
                    endByte,
                    startLine,
                    endLine));
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
                new RawHit(
                    new SegmentId(segment.SegmentId),
                    new DocumentOrdinal(document.Ordinal),
                    start,
                    end,
                    startLine,
                    endLine));
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

internal readonly record struct SearchLimits(
    int MaximumHits,
    int MaximumMatchedDocuments,
    long MaximumBytesScanned)
{
    public static SearchLimits FromQuery(SearchQuery query)
    {
        return new SearchLimits(
            query.MaxHits,
            query.MaxMatchedDocuments,
            query.MaxBytesScanned);
    }
}

internal struct SearchMetrics
{
    public int ScopeDocuments;
    public long ScopeBytes;
    public long PostingsBytesRead;
    public int CandidateDocumentsAfterIndex;
    public int CandidateDocumentsAfterPrecheck;
    public int VerifiedDocuments;
    public long BytesScanned;
    public long DecodedChars;
    public int RegexInvocations;
    public int Utf8RegexInvocations;
    public int FallbackRegexInvocations;
    public int NonBacktrackingRegexInvocations;
    public int Utf8RegexMatchedDocuments;
    public long Utf8RegexMatchesEnumerated;
    public long Utf8RegexCacheHits;
    public long Utf8RegexCacheMisses;

    public static SearchMetrics ForRegex(PreparedRegexExecutionAccess preparedRegex)
    {
        return new SearchMetrics
        {
            Utf8RegexCacheHits = preparedRegex.Utf8RegexCacheHit == true ? 1 : 0,
            Utf8RegexCacheMisses = preparedRegex.Utf8RegexCacheHit == false ? 1 : 0
        };
    }

    public static SearchMetrics FromSearchStats(SearchStats stats)
    {
        return new SearchMetrics
        {
            ScopeDocuments = stats.ScopeDocuments,
            ScopeBytes = stats.ScopeBytes,
            PostingsBytesRead = stats.PostingsBytesRead,
            CandidateDocumentsAfterIndex = stats.CandidateDocumentsAfterIndex,
            CandidateDocumentsAfterPrecheck = stats.CandidateDocumentsAfterPrecheck,
            VerifiedDocuments = stats.VerifiedDocuments,
            BytesScanned = stats.BytesScanned,
            DecodedChars = stats.DecodedChars,
            RegexInvocations = stats.RegexInvocations,
            Utf8RegexInvocations = stats.Utf8RegexInvocations,
            FallbackRegexInvocations = stats.FallbackRegexInvocations,
            NonBacktrackingRegexInvocations = stats.NonBacktrackingRegexInvocations,
            Utf8RegexMatchedDocuments = stats.Utf8RegexMatchedDocuments,
            Utf8RegexMatchesEnumerated = stats.Utf8RegexMatchesEnumerated,
            Utf8RegexCacheHits = stats.Utf8RegexCacheHits,
            Utf8RegexCacheMisses = stats.Utf8RegexCacheMisses
        };
    }

    public void Add(in SearchMetrics other)
    {
        ScopeDocuments += other.ScopeDocuments;
        ScopeBytes += other.ScopeBytes;
        PostingsBytesRead += other.PostingsBytesRead;
        CandidateDocumentsAfterIndex += other.CandidateDocumentsAfterIndex;
        CandidateDocumentsAfterPrecheck += other.CandidateDocumentsAfterPrecheck;
        VerifiedDocuments += other.VerifiedDocuments;
        BytesScanned += other.BytesScanned;
        DecodedChars += other.DecodedChars;
        RegexInvocations += other.RegexInvocations;
        Utf8RegexInvocations += other.Utf8RegexInvocations;
        FallbackRegexInvocations += other.FallbackRegexInvocations;
        NonBacktrackingRegexInvocations += other.NonBacktrackingRegexInvocations;
        Utf8RegexMatchedDocuments += other.Utf8RegexMatchedDocuments;
        Utf8RegexMatchesEnumerated += other.Utf8RegexMatchesEnumerated;
        Utf8RegexCacheHits += other.Utf8RegexCacheHits;
        Utf8RegexCacheMisses += other.Utf8RegexCacheMisses;
    }

    public SearchStats ToSearchStats(
        long elapsedMilliseconds,
        int hitsReturned,
        SearchStopReason stopReason)
    {
        return new SearchStats(new SearchStatsData
        {
            ElapsedMilliseconds = elapsedMilliseconds,
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
            HitsReturned = hitsReturned,
            StopReason = stopReason
        });
    }
}

internal readonly record struct SegmentSearchResult(
    SearchMetrics Metrics,
    int HitsReturned,
    int MatchedDocuments,
    SearchStopReason StopReason);

