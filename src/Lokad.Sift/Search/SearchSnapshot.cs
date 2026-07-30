using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
    private readonly CorpusManifest _manifest;
    private readonly List<SegmentCache> _segments;
    private readonly SiftOptions _options;
    private readonly SearchOptions _searchOptions;
    private readonly StorageReadSnapshot _storageSnapshot;
    private Action? _onDisposed;
    private int _disposed;

    public SearchSnapshot(
        ISiftStorage storage,
        SiftOptions options,
        SearchOptions searchOptions,
        Action? onDisposed = null)
    {
        _options = options;
        _searchOptions = searchOptions;
        _onDisposed = onDisposed;
        _storageSnapshot = storage.OpenReadSnapshot();
        _manifest = _storageSnapshot.Manifest;

        _segments = new List<SegmentCache>(_manifest.Segments.Count);
        try
        {
            foreach (var entry in _manifest.Segments)
            {
                _segments.Add(new SegmentCache(storage.OpenSegment(entry.SegmentId), entry.DeadOrdinals));
            }
        }
        catch
        {
            foreach (var cache in _segments)
            {
                cache.Segment.Dispose();
            }

            _storageSnapshot.Dispose();
            throw;
        }
    }

    public SearchPlan Explain(in SearchQuery query, in PathFilter pathFilter)
    {
        ThrowIfDisposed();
        var resolvedCaseMode = ResolveCaseMode(query);
        var matcher = new PathFilterMatcher(pathFilter);
        var regexPlan = RegexExecutionPlanner.Resolve(query, resolvedCaseMode);
        var anchors = BuildQueryAnchors(query, regexPlan);
        var mandatoryFragments = anchors.GetMandatoryTexts();
        var advisorySeeds = anchors.GetAdvisoryTexts();
        var scopeDocuments = 0;
        long scopeBytes = 0;
        var selected = new List<SelectedNGram>(4);
        var usesIndex = false;
        SearchPlanCandidate? explainedPlan = null;
        long explainedScopeBytes = -1;
        var explainedPathCost = 0d;
        var alternativeSums = new Dictionary<(SearchPlanFamily PlanFamily, SearchCandidateKind CandidateKind, SearchVerifierKind VerifierKind, bool UsesIndex), PlannerAlternativeAccumulator>();

        foreach (var cache in _segments)
        {
            var segmentPlan = SearchPlanner.CreateSegmentPlan(cache, matcher, query, resolvedCaseMode, anchors, regexPlan, _options);
            scopeDocuments += segmentPlan.Scope.Ordinals.Length;
            scopeBytes += segmentPlan.Scope.Bytes;
            if (explainedPlan is null || segmentPlan.Scope.Bytes > explainedScopeBytes)
            {
                explainedPlan = segmentPlan.ContentPlan.ChosenPlan;
                explainedScopeBytes = segmentPlan.Scope.Bytes;
                explainedPathCost = segmentPlan.EstimatedPathFilterCost;
            }

            if (segmentPlan.ContentPlan.ChosenPlan.CandidatePlan.UsesIndex)
            {
                usesIndex = true;
                if (selected.Count == 0)
                {
                    foreach (var gram in segmentPlan.ContentPlan.ChosenPlan.SelectedNgrams)
                    {
                        selected.Add(gram);
                    }
                }
            }

            foreach (var alternative in segmentPlan.ContentPlan.Alternatives)
            {
                var key = (alternative.PlanFamily, alternative.CandidateKind, alternative.VerifierKind, alternative.CandidatePlan.UsesIndex);
                ref var accumulator = ref CollectionsMarshal.GetValueRefOrAddDefault(alternativeSums, key, out _);
                accumulator.Add(alternative);
            }
        }

        var patternKind = query.PatternMode == PatternMode.Regex ? regexPlan.PatternKind : "Literal";
        var canUseLiteralVerifier = query.PatternMode == PatternMode.Literal || regexPlan.IsExactLiteral;
        var canUseNonBacktracking = query.PatternMode == PatternMode.Regex && regexPlan.CanUseNonBacktracking;
        var needsUnicodeDecode = RegexExecutionPlanner.NeedsUnicodeDecode(query, mandatoryFragments, resolvedCaseMode, regexPlan);
        var mayRequireWholeDocumentVerification = query.PatternMode == PatternMode.Regex && !regexPlan.IsExactLiteral || query.WholeLine || query.Multiline;
        var candidateKind = explainedPlan?.CandidateKind ?? RegexExecutionPlanner.GetCandidateKind(query, usesIndex, selected, regexPlan);
        var planFamily = explainedPlan?.PlanFamily ?? RegexExecutionPlanner.GetPlanFamily(query, usesIndex, selected, mandatoryFragments.Length > 0, regexPlan);
        var verifierKind = explainedPlan?.VerifierKind ?? SearchPlanKinds.GetVerifierKind(query, regexPlan, usesIndex);
        var plannerAlternatives = alternativeSums
            .Select(static pair => pair.Value.ToPlannerAlternative(pair.Key.PlanFamily, pair.Key.CandidateKind, pair.Key.VerifierKind, pair.Key.UsesIndex))
            .OrderBy(static alternative => alternative.EstimatedCost)
            .ThenBy(static alternative => alternative.EstimatedCandidateDocuments)
            .ToArray();
        var runnerUp = plannerAlternatives
            .FirstOrDefault(alternative =>
                alternative.PlanFamily != planFamily.ToLabel() ||
                alternative.CandidateKind != candidateKind.ToLabel() ||
                alternative.VerifierKind != verifierKind.ToLabel());
        var hasRunnerUp = !string.IsNullOrEmpty(runnerUp.PlanFamily);

        return new SearchPlan(
            patternKind,
            resolvedCaseMode,
            scopeDocuments,
            scopeBytes,
            mandatoryFragments,
            advisorySeeds,
            [.. selected],
            matcher.PathPrefix is not null,
            !string.IsNullOrWhiteSpace(pathFilter.PathRegex),
            pathFilter.IncludeGlobs is { Length: > 0 },
            pathFilter.ExcludeGlobs is { Length: > 0 },
            matcher.UsesPathIndex,
            usesIndex,
            canUseLiteralVerifier,
            canUseNonBacktracking,
            needsUnicodeDecode,
            mayRequireWholeDocumentVerification,
            explainedPathCost,
            explainedPlan?.EstimatedDirectScanCost ?? 0d,
            explainedPlan?.EstimatedIndexedCost,
            explainedPlan?.EstimatedCandidateDocuments ?? 0d,
            explainedPlan?.EstimatedCost ?? 0d,
            planFamily.ToLabel(),
            candidateKind.ToLabel(),
            verifierKind.ToLabel(),
            hasRunnerUp ? runnerUp.PlanFamily : null,
            hasRunnerUp ? runnerUp.CandidateKind : null,
            hasRunnerUp ? runnerUp.VerifierKind : null,
            hasRunnerUp ? Math.Max(0d, runnerUp.EstimatedCost - (explainedPlan?.EstimatedCost ?? 0d)) : null,
            query.PatternMode == PatternMode.Regex ? query.TimeoutPolicy : TimeoutPolicy.Default,
            plannerAlternatives);
    }

    public SearchStats Search(in SearchQuery query, in PathFilter pathFilter, IHitCollector collector, CancellationToken cancellationToken = default)
    {
        return Search(query, pathFilter, collector, pathVisibility: null, cancellationToken);
    }

    internal SearchStats Search(in SearchQuery query, in PathFilter pathFilter, IHitCollector collector, IPathVisibility? pathVisibility, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return CanUseSegmentParallelSearch(query)
            ? SearchParallel(query, pathFilter, collector, pathVisibility, cancellationToken)
            : SearchSequential(query, pathFilter, collector, pathVisibility, cancellationToken);
    }

    private SearchStats SearchSequential(in SearchQuery query, in PathFilter pathFilter, IHitCollector collector, IPathVisibility? pathVisibility, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collector);

        var matcher = new PathFilterMatcher(pathFilter);
        var resolvedCaseMode = ResolveCaseMode(query);
        var regexPlan = RegexExecutionPlanner.Resolve(query, resolvedCaseMode);
        var anchors = BuildQueryAnchors(query, regexPlan);
        var mandatoryFragments = anchors.GetMandatoryTexts();
        var preparedLiteral = PrepareLiteralQuery(query, resolvedCaseMode, regexPlan.ExactLiteral);
        var preparedMandatoryFragments = PrepareMandatoryFragments(anchors.MandatoryAnchors, resolvedCaseMode);
        var preparedOrderedAnchorGroups = PrepareOrderedAnchorGroups(anchors.OrderedAnchorGroups);
        var watch = Stopwatch.StartNew();
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
        long byteRegexCacheHits = 0;
        long byteRegexCacheMisses = 0;
        var hitsReturned = 0;
        var hitDocuments = 0;
        var stopReason = SearchStopReason.None;

        foreach (var cache in _segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmentPlan = SearchPlanner.CreateSegmentPlan(cache, matcher, query, resolvedCaseMode, anchors, regexPlan, _options);
            scopeDocuments += segmentPlan.Scope.Ordinals.Length;
            scopeBytes += segmentPlan.Scope.Bytes;

            var candidates = CandidateGenerator.Generate(cache.Segment, segmentPlan.Scope.Ordinals, segmentPlan.ContentPlan.ChosenPlan.CandidatePlan);
            var byteRegexStartPrecheckGroups = segmentPlan.ContentPlan.ChosenPlan.CandidateKind == SearchCandidateKind.DirectScan
                ? preparedOrderedAnchorGroups
                : [];
            postingsBytesRead += candidates.PostingsBytesRead;
            candidateDocumentsAfterIndex += candidates.Ordinals.Length;
            var useOrderedAnchorPrecheck = ShouldUseOrderedAnchorPrecheck(preparedOrderedAnchorGroups, candidates.Ordinals.Length);

            foreach (var ordinal in candidates.Ordinals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = cache.Segment.GetDocument(ordinal);
                if (pathVisibility is not null && !pathVisibility.IsVisible(cache.Segment.GetPathBytes(document)))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(query.Pattern))
                {
                    candidateDocumentsAfterPrecheck++;
                    verifiedDocuments++;
                    if (!collector.OnHit(new RawHit(cache.Segment.SegmentId, document.Ordinal, 0, 0, 1, 1)))
                    {
                        stopReason = SearchStopReason.Cancelled;
                        goto Done;
                    }

                    hitsReturned++;
                    hitDocuments++;
                }
                else
                {
                    bytesScanned += document.ContentLength;
                    if (bytesScanned > query.MaxBytesScanned)
                    {
                        stopReason = SearchStopReason.MaxBytesScanned;
                        goto Done;
                    }

                    var contentBytes = cache.Segment.GetContentBytes(document);
                    if (!PassesMandatoryPrecheck(contentBytes, mandatoryFragments, preparedMandatoryFragments, resolvedCaseMode, ref decodedChars, out var predecodedText))
                    {
                        continue;
                    }

                    if (useOrderedAnchorPrecheck && !PassesOrderedAnchorPrecheck(contentBytes, preparedOrderedAnchorGroups))
                    {
                        continue;
                    }

                    candidateDocumentsAfterPrecheck++;
                    var countBefore = hitsReturned;
                    if (query.PatternMode == PatternMode.Literal || regexPlan.IsExactLiteral)
                    {
                        SearchLiteralDocument(cache.Segment, document, query, resolvedCaseMode, preparedLiteral, collector, ref decodedChars, ref hitsReturned, ref stopReason, predecodedText, regexPlan.ExactLiteral);
                    }
                    else if (regexPlan.UsesUtf8Regex && regexPlan.Utf8RegexProgram is not null && CanUseUtf8RegexForDocument(document))
                    {
                        Utf8RegexEngine.TrySearchDocument(cache.Segment, document, regexPlan.Utf8RegexProgram, byteRegexStartPrecheckGroups, collector, ref hitsReturned, ref byteRegexInvocations, ref byteRegexMatchedDocuments, ref byteRegexStartPositionsTried, ref byteRegexCacheHits, ref byteRegexCacheMisses, ref stopReason);
                    }
                    else
                    {
                        RegexVerifier.TrySearchDocument(cache.Segment, document, query, resolvedCaseMode, collector, ref decodedChars, ref hitsReturned, ref fallbackRegexInvocations, ref nonBacktrackingRegexInvocations, ref stopReason, predecodedText);
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

    private SearchStats SearchParallel(in SearchQuery query, in PathFilter pathFilter, IHitCollector collector, IPathVisibility? pathVisibility, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collector);

        var matcher = new PathFilterMatcher(pathFilter);
        var resolvedCaseMode = ResolveCaseMode(query);
        var regexPlan = RegexExecutionPlanner.Resolve(query, resolvedCaseMode);
        var anchors = BuildQueryAnchors(query, regexPlan);
        var preparedLiteral = PrepareLiteralQuery(query, resolvedCaseMode, regexPlan.ExactLiteral);
        var preparedMandatoryFragments = PrepareMandatoryFragments(anchors.MandatoryAnchors, resolvedCaseMode);
        var preparedOrderedAnchorGroups = PrepareOrderedAnchorGroups(anchors.OrderedAnchorGroups);
        var watch = Stopwatch.StartNew();
        var results = new SegmentSearchResult[_segments.Count];
        var queryCopy = query;
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = _searchOptions.SegmentParallelism
        };

        Parallel.For(0, _segments.Count, options, index =>
        {
            results[index] = SearchSegmentFully(_segments[index], queryCopy, matcher, resolvedCaseMode, anchors, preparedLiteral, preparedMandatoryFragments, preparedOrderedAnchorGroups, regexPlan, pathVisibility, cancellationToken);
        });

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
        long byteRegexCacheHits = 0;
        long byteRegexCacheMisses = 0;
        var hitsReturned = 0;
        var hitDocuments = 0;
        var stopReason = SearchStopReason.None;

        for (var i = 0; i < results.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = results[i];
            scopeDocuments += result.ScopeDocuments;
            scopeBytes += result.ScopeBytes;
            postingsBytesRead += result.PostingsBytesRead;
            candidateDocumentsAfterIndex += result.CandidateDocumentsAfterIndex;

            for (var docIndex = 0; docIndex < result.Documents.Count; docIndex++)
            {
                var document = result.Documents[docIndex];
                bytesScanned += document.BytesScanned;
                if (bytesScanned > query.MaxBytesScanned)
                {
                    stopReason = SearchStopReason.MaxBytesScanned;
                    goto Done;
                }

                candidateDocumentsAfterPrecheck += document.CandidateDocumentsAfterPrecheck;
                verifiedDocuments += document.VerifiedDocuments;
                decodedChars += document.DecodedChars;
                regexInvocations += document.RegexInvocations;
                byteRegexInvocations += document.Utf8RegexInvocations;
                fallbackRegexInvocations += document.FallbackRegexInvocations;
                nonBacktrackingRegexInvocations += document.NonBacktrackingRegexInvocations;
                byteRegexMatchedDocuments += document.Utf8RegexMatchedDocuments;
                byteRegexStartPositionsTried += document.Utf8RegexMatchesEnumerated;
                byteRegexCacheHits += document.Utf8RegexCacheHits;
                byteRegexCacheMisses += document.Utf8RegexCacheMisses;

                var countBefore = hitsReturned;
                for (var hitIndex = 0; hitIndex < document.Hits.Count; hitIndex++)
                {
                    if (!collector.OnHit(document.Hits[hitIndex]))
                    {
                        stopReason = SearchStopReason.Cancelled;
                        goto Done;
                    }

                    hitsReturned++;
                }

                if (hitsReturned > countBefore)
                {
                    hitDocuments++;
                }

                if (document.StopReason != SearchStopReason.None)
                {
                    stopReason = document.StopReason;
                    goto Done;
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
        ThrowIfDisposed();
        var cache = GetSegment(hit.SegmentId);

        var document = cache.Segment.GetDocument(hit.DocumentOrdinal);
        var path = cache.Segment.GetPath(document);
        var contentBytes = cache.Segment.GetContentBytes(document);
        var encodedLineIndex = cache.Segment.GetEncodedLineIndexBytes(document);
        var startLine = hit.StartLine > 0 ? hit.StartLine : LineMap.GetLineFromByteOffset(encodedLineIndex, hit.StartByte);
        var endLine = hit.EndLine > 0 ? hit.EndLine : LineMap.GetLineFromByteOffset(encodedLineIndex, Math.Max(hit.StartByte, hit.EndByte == 0 ? hit.StartByte : hit.EndByte - 1));
        var asciiDocument = (document.Flags & (1 << 1)) != 0;
        var startColumn = GetUtf16Column(contentBytes, encodedLineIndex, hit.StartByte, startLine, asciiDocument);
        var endColumn = GetUtf16Column(contentBytes, encodedLineIndex, hit.EndByte, endLine, asciiDocument);
        var matchText = hit.EndByte > hit.StartByte ? DecodeText(contentBytes[hit.StartByte..hit.EndByte], asciiDocument, trimLineEnd: false) : string.Empty;

        return new MaterializedHit(
            path,
            startLine,
            startColumn,
            endLine,
            endColumn,
            matchText,
            CollectContext(contentBytes, encodedLineIndex, startLine - contextBefore, startLine - 1, asciiDocument),
            CollectContext(contentBytes, encodedLineIndex, endLine + 1, endLine + contextAfter, asciiDocument));
    }

    public bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content)
    {
        ThrowIfDisposed();
        var normalizedPath = IndexBuilder.CanonicalizePath(key.Path);

        foreach (var cache in _segments)
        {
            if (!cache.Segment.TryFindDocumentOrdinal(normalizedPath, out var ordinal) || IsDeadOrdinal(cache.DeadOrdinals, ordinal))
            {
                continue;
            }

            var document = cache.Segment.GetDocument(ordinal);
            content = cache.Segment.GetContentMemory(document);
            return true;
        }

        content = default;
        return false;
    }

    internal string GetPath(in RawHit hit)
    {
        var cache = GetSegment(hit.SegmentId);
        var document = cache.Segment.GetDocument(hit.DocumentOrdinal);
        return cache.Segment.GetPath(document);
    }

    internal ulong[] GetSegmentIds()
    {
        var ids = GC.AllocateUninitializedArray<ulong>(_segments.Count);
        for (var i = 0; i < _segments.Count; i++)
        {
            ids[i] = _segments[i].Segment.SegmentId;
        }

        return ids;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            foreach (var cache in _segments)
            {
                cache.Segment.Dispose();
            }
        }
        finally
        {
            _storageSnapshot.Dispose();
            Interlocked.Exchange(ref _onDisposed, null)?.Invoke();
        }
    }

    private SegmentCache GetSegment(ulong segmentId)
    {
        foreach (var candidate in _segments)
        {
            if (candidate.Segment.SegmentId == segmentId)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Unknown segment id {segmentId}.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private static bool IsDeadOrdinal(uint[] deadOrdinals, uint ordinal)
    {
        return Array.BinarySearch(deadOrdinals, ordinal) >= 0;
    }

    private SegmentSearchResult SearchSegmentFully(SegmentCache cache, in SearchQuery query, PathFilterMatcher matcher, CaseMode resolvedCaseMode, in SearchQueryAnchors anchors, PreparedLiteralQuery? preparedLiteral, PreparedMandatoryFragment[] preparedMandatoryFragments, PreparedOrderedAnchor[][] preparedOrderedAnchorGroups, RegexExecutionPlan regexPlan, IPathVisibility? pathVisibility, CancellationToken cancellationToken)
    {
        var segmentPlan = SearchPlanner.CreateSegmentPlan(cache, matcher, query, resolvedCaseMode, anchors, regexPlan, _options);
        var scope = segmentPlan.Scope;
        var mandatoryFragments = anchors.GetMandatoryTexts();
        var postingsBytesRead = 0L;
        var candidateDocumentsAfterIndex = 0;
        var candidates = CandidateGenerator.Generate(cache.Segment, scope.Ordinals, segmentPlan.ContentPlan.ChosenPlan.CandidatePlan);
        var byteRegexStartPrecheckGroups = segmentPlan.ContentPlan.ChosenPlan.CandidateKind == SearchCandidateKind.DirectScan
            ? preparedOrderedAnchorGroups
            : [];
        postingsBytesRead = candidates.PostingsBytesRead;
        candidateDocumentsAfterIndex = candidates.Ordinals.Length;
        var useOrderedAnchorPrecheck = ShouldUseOrderedAnchorPrecheck(preparedOrderedAnchorGroups, candidates.Ordinals.Length);
        var documents = new List<DocumentSearchResult>(candidates.Ordinals.Length);

        foreach (var ordinal in candidates.Ordinals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = cache.Segment.GetDocument(ordinal);
            if (pathVisibility is not null && !pathVisibility.IsVisible(cache.Segment.GetPathBytes(document)))
            {
                continue;
            }

            if (string.IsNullOrEmpty(query.Pattern))
            {
                documents.Add(new DocumentSearchResult(
                    BytesScanned: 0,
                    CandidateDocumentsAfterPrecheck: 1,
                    VerifiedDocuments: 1,
                    DecodedChars: 0,
                    RegexInvocations: 0,
                    Utf8RegexInvocations: 0,
                    FallbackRegexInvocations: 0,
                    NonBacktrackingRegexInvocations: 0,
                    Utf8RegexMatchedDocuments: 0,
                    Utf8RegexMatchesEnumerated: 0,
                    Utf8RegexCacheHits: 0,
                    Utf8RegexCacheMisses: 0,
                    StopReason: SearchStopReason.None,
                    Hits: [new RawHit(cache.Segment.SegmentId, document.Ordinal, 0, 0, 1, 1)]));
                continue;
            }

            var collector = new BufferingHitCollector();
            long decodedChars = 0;
            var regexInvocations = 0;
            var byteRegexInvocations = 0;
            var fallbackRegexInvocations = 0;
            var nonBacktrackingRegexInvocations = 0;
            var byteRegexMatchedDocuments = 0;
            long byteRegexStartPositionsTried = 0;
            long byteRegexCacheHits = 0;
            long byteRegexCacheMisses = 0;
            var hitsReturned = 0;
            var stopReason = SearchStopReason.None;
            var contentBytes = cache.Segment.GetContentBytes(document);
            if (!PassesMandatoryPrecheck(contentBytes, mandatoryFragments, preparedMandatoryFragments, resolvedCaseMode, ref decodedChars, out var predecodedText))
            {
                documents.Add(new DocumentSearchResult(
                    BytesScanned: document.ContentLength,
                    CandidateDocumentsAfterPrecheck: 0,
                    VerifiedDocuments: 0,
                    DecodedChars: decodedChars,
                    RegexInvocations: 0,
                    Utf8RegexInvocations: 0,
                    FallbackRegexInvocations: 0,
                    NonBacktrackingRegexInvocations: 0,
                    Utf8RegexMatchedDocuments: 0,
                    Utf8RegexMatchesEnumerated: 0,
                    Utf8RegexCacheHits: 0,
                    Utf8RegexCacheMisses: 0,
                    StopReason: SearchStopReason.None,
                    Hits: collector.Hits));
                continue;
            }

            if (useOrderedAnchorPrecheck && !PassesOrderedAnchorPrecheck(contentBytes, preparedOrderedAnchorGroups))
            {
                documents.Add(new DocumentSearchResult(
                    BytesScanned: document.ContentLength,
                    CandidateDocumentsAfterPrecheck: 0,
                    VerifiedDocuments: 0,
                    DecodedChars: decodedChars,
                    RegexInvocations: 0,
                    Utf8RegexInvocations: 0,
                    FallbackRegexInvocations: 0,
                    NonBacktrackingRegexInvocations: 0,
                    Utf8RegexMatchedDocuments: 0,
                    Utf8RegexMatchesEnumerated: 0,
                    Utf8RegexCacheHits: 0,
                    Utf8RegexCacheMisses: 0,
                    StopReason: SearchStopReason.None,
                    Hits: collector.Hits));
                continue;
            }

            if (query.PatternMode == PatternMode.Literal || regexPlan.IsExactLiteral)
            {
                SearchLiteralDocument(cache.Segment, document, query, resolvedCaseMode, preparedLiteral, collector, ref decodedChars, ref hitsReturned, ref stopReason, predecodedText, regexPlan.ExactLiteral);
            }
            else if (regexPlan.UsesUtf8Regex && regexPlan.Utf8RegexProgram is not null && CanUseUtf8RegexForDocument(document))
            {
                Utf8RegexEngine.TrySearchDocument(cache.Segment, document, regexPlan.Utf8RegexProgram, byteRegexStartPrecheckGroups, collector, ref hitsReturned, ref byteRegexInvocations, ref byteRegexMatchedDocuments, ref byteRegexStartPositionsTried, ref byteRegexCacheHits, ref byteRegexCacheMisses, ref stopReason);
            }
            else
            {
                RegexVerifier.TrySearchDocument(cache.Segment, document, query, resolvedCaseMode, collector, ref decodedChars, ref hitsReturned, ref fallbackRegexInvocations, ref nonBacktrackingRegexInvocations, ref stopReason, predecodedText);
            }
            regexInvocations = byteRegexInvocations + fallbackRegexInvocations;

            documents.Add(new DocumentSearchResult(
                BytesScanned: document.ContentLength,
                CandidateDocumentsAfterPrecheck: 1,
                VerifiedDocuments: 1,
                DecodedChars: decodedChars,
                RegexInvocations: regexInvocations,
                Utf8RegexInvocations: byteRegexInvocations,
                FallbackRegexInvocations: fallbackRegexInvocations,
                NonBacktrackingRegexInvocations: nonBacktrackingRegexInvocations,
                Utf8RegexMatchedDocuments: byteRegexMatchedDocuments,
                Utf8RegexMatchesEnumerated: byteRegexStartPositionsTried,
                Utf8RegexCacheHits: byteRegexCacheHits,
                Utf8RegexCacheMisses: byteRegexCacheMisses,
                StopReason: stopReason,
                Hits: collector.Hits));

            if (stopReason != SearchStopReason.None)
            {
                break;
            }
        }

        return new SegmentSearchResult(
            scope.Ordinals.Length,
            scope.Bytes,
            postingsBytesRead,
            candidateDocumentsAfterIndex,
            documents);
    }

    private static void SearchLiteralDocument(MappedSegment segment, SegmentDocument document, in SearchQuery query, CaseMode resolvedCaseMode, PreparedLiteralQuery? preparedLiteral, IHitCollector collector, ref long decodedChars, ref int hitsReturned, ref SearchStopReason stopReason, string? predecodedText = null, string? literalOverride = null)
    {
        var contentBytes = segment.GetContentBytes(document);
        var encodedLineIndex = segment.GetEncodedLineIndexBytes(document);
        var pattern = literalOverride ?? query.Pattern;
        if (string.IsNullOrEmpty(pattern))
        {
            if (!collector.OnHit(new RawHit(segment.SegmentId, document.Ordinal, 0, 0, 1, 1)))
            {
                stopReason = SearchStopReason.Cancelled;
            }

            hitsReturned++;
            return;
        }

        var needle = preparedLiteral?.Needle ?? Encoding.UTF8.GetBytes(pattern);
        var asciiNeedle = preparedLiteral?.IsAscii ?? needle.AsSpan().IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0;
        var asciiDocument = query.WholeWord && asciiNeedle && contentBytes.IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F) < 0;

        if (resolvedCaseMode == CaseMode.Sensitive &&
            asciiNeedle &&
            (!query.WholeWord && !query.WholeLine || asciiDocument || query.WholeLine))
        {
            SearchAsciiLiteralDocument(segment, document, encodedLineIndex, contentBytes, needle, query.WholeWord && asciiDocument, query.WholeLine, caseSensitive: true, collector, ref hitsReturned, ref stopReason);
            return;
        }

        if (resolvedCaseMode == CaseMode.Insensitive &&
            asciiNeedle &&
            (!query.WholeWord && !query.WholeLine || asciiDocument || query.WholeLine))
        {
            var loweredNeedle = preparedLiteral?.LoweredAsciiNeedle ?? LowerAscii(needle);
            SearchAsciiLiteralDocument(segment, document, encodedLineIndex, contentBytes, loweredNeedle, query.WholeWord && asciiDocument, query.WholeLine, caseSensitive: false, collector, ref hitsReturned, ref stopReason);
            return;
        }

        var text = predecodedText ?? Encoding.UTF8.GetString(contentBytes);
        if (predecodedText is null)
        {
            decodedChars += text.Length;
        }
        var comparison = resolvedCaseMode == CaseMode.Sensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var searchIndex = 0;

        while (searchIndex <= text.Length)
        {
            var found = text.IndexOf(pattern, searchIndex, comparison);
            if (found < 0)
            {
                return;
            }

            var end = found + pattern.Length;
            if (query.WholeWord && !IsWholeWord(text, found, end))
            {
                searchIndex = found + 1;
                continue;
            }

            if (query.WholeLine && !IsWholeLine(text, found, end))
            {
                searchIndex = found + 1;
                continue;
            }

            var startByte = Encoding.UTF8.GetByteCount(text.AsSpan(0, found));
            var endByte = startByte + Encoding.UTF8.GetByteCount(text.AsSpan(found, pattern.Length));
            var startLine = LineMap.GetLineFromByteOffset(encodedLineIndex, startByte);
            var endLine = LineMap.GetLineFromByteOffset(encodedLineIndex, Math.Max(startByte, endByte - 1));
            if (!collector.OnHit(new RawHit(segment.SegmentId, document.Ordinal, startByte, endByte, startLine, endLine)))
            {
                stopReason = SearchStopReason.Cancelled;
                return;
            }

            hitsReturned++;
            searchIndex = found + 1;
        }
    }

    private static void SearchAsciiLiteralDocument(MappedSegment segment, SegmentDocument document, ReadOnlySpan<byte> encodedLineIndex, ReadOnlySpan<byte> contentBytes, ReadOnlySpan<byte> needle, bool wholeWord, bool wholeLine, bool caseSensitive, IHitCollector collector, ref int hitsReturned, ref SearchStopReason stopReason)
    {
        var offset = 0;
        while (offset <= contentBytes.Length - needle.Length)
        {
            var relativeIndex = caseSensitive
                ? contentBytes[offset..].IndexOf(needle)
                : IndexOfAsciiInsensitive(contentBytes[offset..], needle);
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
            if (!collector.OnHit(new RawHit(segment.SegmentId, document.Ordinal, start, end, startLine, endLine)))
            {
                stopReason = SearchStopReason.Cancelled;
                return;
            }

            hitsReturned++;
            offset = start + 1;
        }
    }

    private static bool PassesMandatoryPrecheck(ReadOnlySpan<byte> contentBytes, string[] mandatoryFragments, PreparedMandatoryFragment[] preparedMandatoryFragments, CaseMode resolvedCaseMode, ref long decodedChars, out string? predecodedText)
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
                if (predecodedText.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var fragment in preparedMandatoryFragments)
        {
            var found = false;
            for (var i = 0; i <= contentBytes.Length - fragment.LoweredAsciiBytes!.Length; i++)
            {
                var matches = true;
                for (var j = 0; j < fragment.LoweredAsciiBytes!.Length; j++)
                {
                    var current = contentBytes[i + j];
                    current = current is >= (byte)'A' and <= (byte)'Z' ? (byte)(current + 32) : current;
                    if (current != fragment.LoweredAsciiBytes[j])
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static bool PassesOrderedAnchorPrecheck(ReadOnlySpan<byte> contentBytes, PreparedOrderedAnchor[][] preparedOrderedAnchorGroups)
    {
        if (preparedOrderedAnchorGroups.Length == 0)
        {
            return true;
        }

        for (var groupIndex = 0; groupIndex < preparedOrderedAnchorGroups.Length; groupIndex++)
        {
            var group = preparedOrderedAnchorGroups[groupIndex];
            var position = 0;
            var matched = true;
            for (var anchorIndex = 0; anchorIndex < group.Length; anchorIndex++)
            {
                var anchor = group[anchorIndex];
                if (position > contentBytes.Length)
                {
                    matched = false;
                    break;
                }

                var sliceLength = contentBytes.Length - position;
                if (anchorIndex > 0)
                {
                    if (anchor.MaxGapBytesFromPrevious is { } maxGap)
                    {
                        sliceLength = Math.Min(sliceLength, maxGap + anchor.Utf8Bytes.Length);
                    }

                    if (anchor.SameLineFromPrevious)
                    {
                        var newlineOffset = IndexOfLineBreak(contentBytes[position..(position + sliceLength)]);
                        if (newlineOffset >= 0)
                        {
                            sliceLength = newlineOffset;
                        }
                    }
                }

                if (sliceLength < anchor.Utf8Bytes.Length)
                {
                    matched = false;
                    break;
                }

                var slice = contentBytes.Slice(position, sliceLength);
                var offset = anchor.LoweredAsciiBytes is null
                    ? slice.IndexOf(anchor.Utf8Bytes)
                    : IndexOfAsciiInsensitive(slice, anchor.LoweredAsciiBytes);
                if (offset < 0)
                {
                    matched = false;
                    break;
                }

                position += offset + anchor.Utf8Bytes.Length;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
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

    private static int IndexOfLineBreak(ReadOnlySpan<byte> haystack)
    {
        for (var i = 0; i < haystack.Length; i++)
        {
            if (haystack[i] is (byte)'\r' or (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfAsciiInsensitive(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> loweredNeedle)
    {
        if (loweredNeedle.Length == 0)
        {
            return 0;
        }

        for (var start = 0; start <= haystack.Length - loweredNeedle.Length; start++)
        {
            var matches = true;
            for (var i = 0; i < loweredNeedle.Length; i++)
            {
                if (FoldAscii(haystack[start + i]) != loweredNeedle[i])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return start;
            }
        }

        return -1;
    }

    private static byte FoldAscii(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
    }

    private static byte[] LowerAscii(ReadOnlySpan<byte> needle)
    {
        var loweredNeedle = GC.AllocateUninitializedArray<byte>(needle.Length);
        for (var i = 0; i < needle.Length; i++)
        {
            loweredNeedle[i] = FoldAscii(needle[i]);
        }

        return loweredNeedle;
    }

    private static PreparedLiteralQuery? PrepareLiteralQuery(in SearchQuery query, CaseMode resolvedCaseMode, string? literalOverride = null)
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
        return new PreparedLiteralQuery(needle, loweredAsciiNeedle, isAscii);
    }

    private static PreparedMandatoryFragment[] PrepareMandatoryFragments(SearchLiteralAnchor[] mandatoryAnchors, CaseMode resolvedCaseMode)
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
            prepared[i] = new PreparedMandatoryFragment(
                text,
                utf8Bytes,
                resolvedCaseMode == CaseMode.Insensitive && isAscii ? LowerAscii(utf8Bytes) : null,
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
                prepared[groupIndex][anchorIndex] = new PreparedOrderedAnchor(anchor.Utf8Bytes, lowered, anchor.MaxGapBytesFromPrevious, anchor.SameLineFromPrevious);
            }
        }

        return prepared;
    }

    private static SearchQueryAnchors BuildQueryAnchors(in SearchQuery query, in RegexExecutionPlan regexPlan)
    {
        var mandatoryFragments = GetMandatoryFragments(query, regexPlan.ExactLiteral);
        var advisorySeeds = GetAdvisorySeeds(query, regexPlan.ExactLiteral, mandatoryFragments, out var advisoryCaseMode);
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

        var regexGroups = regexPlan.Utf8RegexProgram?.OrderedAnchorGroups;
        if (regexGroups is null || regexGroups.Length == 0)
        {
            return new SearchQueryAnchors(mandatoryAnchors, advisoryAnchors, advisoryCaseMode, []);
        }

        var orderedAnchorGroups = new SearchOrderedAnchorGroup[regexGroups.Length];
        var ignoreCase = regexPlan.Utf8RegexProgram!.IgnoreCase;
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

    private static string[] GetMandatoryFragments(in SearchQuery query, string? exactRegexLiteral = null)
    {
        if (query.PatternMode == PatternMode.Regex)
        {
            return exactRegexLiteral is not null
                ? [exactRegexLiteral]
                : PruneRedundantMandatoryFragments(RegexFragmentAnalyzer.ExtractMandatoryFragments(query.Pattern));
        }

        return string.IsNullOrEmpty(query.Pattern) ? [] : [query.Pattern];
    }

    private static string[] GetAdvisorySeeds(in SearchQuery query, string? exactRegexLiteral, string[] mandatoryFragments, out CaseMode advisoryCaseMode)
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

        var advisoryPlan = RegexFragmentAnalyzer.AnalyzeAdvisorySeeds(query.Pattern);
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

    private static CaseMode ResolveCaseMode(in SearchQuery query)
    {
        return query.CaseMode switch
        {
            CaseMode.Sensitive => CaseMode.Sensitive,
            CaseMode.Insensitive => CaseMode.Insensitive,
            CaseMode.Smart => Hashing.ContainsUppercase(query.Pattern) ? CaseMode.Sensitive : CaseMode.Insensitive,
            _ => CaseMode.Sensitive
        };
    }

    private static bool IsWholeWord(string text, int start, int end)
    {
        return IsBoundary(text, start - 1) && IsBoundary(text, end);
    }

    private static bool IsWholeWordAscii(ReadOnlySpan<byte> contentBytes, int start, int end)
    {
        return IsBoundaryAscii(contentBytes, start - 1) && IsBoundaryAscii(contentBytes, end);
    }

    private static bool IsBoundary(string text, int index)
    {
        if (index < 0 || index >= text.Length)
        {
            return true;
        }

        return !char.IsLetterOrDigit(text[index]) && text[index] != '_';
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

    private static int GetUtf16Column(ReadOnlySpan<byte> contentBytes, ReadOnlySpan<byte> encodedLineIndex, int byteOffset, int oneBasedLine, bool asciiDocument)
    {
        var start = LineMap.GetLineStart(encodedLineIndex, oneBasedLine);
        if (asciiDocument)
        {
            return Math.Min(byteOffset, contentBytes.Length) - start + 1;
        }

        return Encoding.UTF8.GetCharCount(contentBytes[start..Math.Min(byteOffset, contentBytes.Length)]) + 1;
    }

    private static string[] CollectContext(ReadOnlySpan<byte> contentBytes, ReadOnlySpan<byte> encodedLineIndex, int startLine, int endLine, bool asciiDocument)
    {
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

        var lines = new string[endLine - startLine + 1];
        var cursor = 0;
        for (var line = startLine; line <= endLine; line++)
        {
            var start = LineMap.GetLineStart(encodedLineIndex, line);
            var end = LineMap.GetLineEndExclusive(encodedLineIndex, line, contentBytes.Length);
            lines[cursor++] = DecodeText(contentBytes[start..end], asciiDocument, trimLineEnd: true);
        }

        return lines;
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

    private bool CanUseSegmentParallelSearch(in SearchQuery query)
    {
        return _searchOptions.SegmentParallelism > 1 &&
               _segments.Count > 1;
    }
}

internal readonly record struct SegmentScope(
    uint[] Ordinals,
    long Bytes);

internal interface IPathVisibility
{
    bool IsVisible(ReadOnlySpan<byte> normalizedPathUtf8);
}

internal sealed record SegmentCache(
    MappedSegment Segment,
    uint[] DeadOrdinals);

internal sealed class PreparedLiteralQuery
{
    public PreparedLiteralQuery(byte[] needle, byte[]? loweredAsciiNeedle, bool isAscii)
    {
        Needle = needle;
        LoweredAsciiNeedle = loweredAsciiNeedle;
        IsAscii = isAscii;
    }

    public byte[] Needle { get; }
    public byte[]? LoweredAsciiNeedle { get; }
    public bool IsAscii { get; }
}

internal sealed class PreparedMandatoryFragment
{
    public PreparedMandatoryFragment(string text, byte[] utf8Bytes, byte[]? loweredAsciiBytes, bool isAscii)
    {
        Text = text;
        Utf8Bytes = utf8Bytes;
        LoweredAsciiBytes = loweredAsciiBytes;
        IsAscii = isAscii;
    }

    public string Text { get; }
    public byte[] Utf8Bytes { get; }
    public byte[]? LoweredAsciiBytes { get; }
    public bool IsAscii { get; }
}

internal sealed class PreparedOrderedAnchor
{
    public PreparedOrderedAnchor(byte[] utf8Bytes, byte[]? loweredAsciiBytes, int? maxGapBytesFromPrevious, bool sameLineFromPrevious)
    {
        Utf8Bytes = utf8Bytes;
        LoweredAsciiBytes = loweredAsciiBytes;
        MaxGapBytesFromPrevious = maxGapBytesFromPrevious;
        SameLineFromPrevious = sameLineFromPrevious;
    }

    public byte[] Utf8Bytes { get; }

    public byte[]? LoweredAsciiBytes { get; }

    public int? MaxGapBytesFromPrevious { get; }

    public bool SameLineFromPrevious { get; }
}

internal sealed class BufferingHitCollector : IHitCollector
{
    public List<RawHit> Hits { get; } = [];

    public bool OnHit(in RawHit hit)
    {
        Hits.Add(hit);
        return true;
    }
}

internal readonly record struct SegmentSearchResult(
    int ScopeDocuments,
    long ScopeBytes,
    long PostingsBytesRead,
    int CandidateDocumentsAfterIndex,
    List<DocumentSearchResult> Documents);

internal readonly record struct DocumentSearchResult(
    int BytesScanned,
    int CandidateDocumentsAfterPrecheck,
    int VerifiedDocuments,
    long DecodedChars,
    int RegexInvocations,
    int Utf8RegexInvocations,
    int FallbackRegexInvocations,
    int NonBacktrackingRegexInvocations,
    int Utf8RegexMatchedDocuments,
    long Utf8RegexMatchesEnumerated,
    long Utf8RegexCacheHits,
    long Utf8RegexCacheMisses,
    SearchStopReason StopReason,
    List<RawHit> Hits);

internal struct PlannerAlternativeAccumulator
{
    public double EstimatedDirectScanCost;
    public double? EstimatedIndexedCost;
    public double EstimatedCandidateDocuments;
    public double EstimatedCost;
    public int SegmentCount;

    public void Add(in SearchPlanCandidate plan)
    {
        EstimatedDirectScanCost += plan.EstimatedDirectScanCost;
        if (plan.EstimatedIndexedCost is double estimatedIndexedCost)
        {
            EstimatedIndexedCost = (EstimatedIndexedCost ?? 0d) + estimatedIndexedCost;
        }

        EstimatedCandidateDocuments += plan.EstimatedCandidateDocuments;
        EstimatedCost += plan.EstimatedCost;
        SegmentCount++;
    }

    public PlannerAlternative ToPlannerAlternative(SearchPlanFamily planFamily, SearchCandidateKind candidateKind, SearchVerifierKind verifierKind, bool usesIndex)
    {
        return new PlannerAlternative(
            planFamily.ToLabel(),
            candidateKind.ToLabel(),
            verifierKind.ToLabel(),
            usesIndex,
            EstimatedDirectScanCost,
            EstimatedIndexedCost,
            EstimatedCandidateDocuments,
            EstimatedCost,
            SegmentCount);
    }
}

