using System.Diagnostics;
using System.Text;
using Lokad.Sift;
using Lokad.Sift.Indexer;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal sealed class OverlaySnapshot : IOverlaySnapshot
{
    private readonly SearchSnapshot _baseSnapshot;
    private readonly Sift _overlayIndex;
    private readonly SiftOptions _options;
    private readonly SearchOptions _searchOptions;
    private readonly Dictionary<string, long> _overlayDocuments = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletedPaths = new(StringComparer.Ordinal);
    private readonly HashSet<ulong> _baseSegmentIds;
    private readonly Dictionary<ulong, ulong> _overlayToSyntheticSegmentIds = [];
    private readonly Dictionary<ulong, ulong> _syntheticToOverlaySegmentIds = [];
    // Collectors may materialize overlay hits recursively while Search holds a read lease.
    private readonly ReaderWriterLockSlim _stateLock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly object _syntheticSync = new();
    private IIndexSnapshot _overlaySnapshot;
    private HiddenPathVisibility _hiddenPathVisibility = HiddenPathVisibility.Empty;
    private ulong _nextSyntheticSegmentId = ulong.MaxValue;
    private int _disposed;

    internal OverlaySnapshot(SearchSnapshot baseSnapshot, SiftOptions options, SearchOptions searchOptions)
    {
        _baseSnapshot = baseSnapshot;
        _options = options;
        _searchOptions = searchOptions;
        _overlayIndex = Sift.CreateInMemory(options);
        _overlaySnapshot = _overlayIndex.OpenSnapshot(searchOptions);
        _baseSegmentIds = new HashSet<ulong>(_baseSnapshot.GetSegmentIds());
    }

    public Task<UpdateStats> Apply(IDocumentSource upserts, IReadOnlyList<DocumentKey> deletions)
    {
        return Apply(
            upserts,
            deletions,
            new OverlayApplyOptions(_options.RejectedUpsertPolicy),
            CancellationToken.None);
    }

    public Task<UpdateStats> Apply(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        CancellationToken cancellationToken)
    {
        return Apply(
            upserts,
            deletions,
            new OverlayApplyOptions(_options.RejectedUpsertPolicy),
            cancellationToken);
    }

    public Task<UpdateStats> Apply(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayApplyOptions options)
    {
        return Apply(upserts, deletions, options, CancellationToken.None);
    }

    public Task<UpdateStats> Apply(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayApplyOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.RejectedUpsertPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RejectedUpsertPolicy is invalid.");
        }

        return ApplyCore(upserts, deletions, options, cancellationToken);
    }

    private async Task<UpdateStats> ApplyCore(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayApplyOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await ApplyCoreUnderLock(upserts, deletions, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task<UpdateStats> ApplyCoreUnderLock(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayApplyOptions options,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var load = IndexBuilder.LoadDocuments(
            upserts,
            cancellationToken,
            _options,
            options.RejectedUpsertPolicy,
            retainContentBeyondOperation: true);
        var deletionSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deletion in deletions)
        {
            var normalized = deletion.Path;
            if (!deletionSet.Add(normalized))
            {
                throw new InvalidOperationException($"Duplicate deletion path in overlay batch: '{normalized}'.");
            }
        }

        for (var i = 0; i < load.Documents.Count; i++)
        {
            var path = load.Documents[i].Path;
            if (deletionSet.Contains(path))
            {
                throw new InvalidOperationException($"Path '{path}' cannot appear in both upserts and deletions.");
            }
        }

        for (var i = 0; i < load.RejectedDocuments.Count; i++)
        {
            var path = load.RejectedDocuments[i].Path;
            if (deletionSet.Contains(path))
            {
                throw new InvalidOperationException($"Path '{path}' cannot appear in both upserts and deletions.");
            }
        }

        if (options.RejectedUpsertPolicy == RejectedUpsertPolicy.DeleteExisting)
        {
            deletionSet.UnionWith(load.RejectedDocuments.Select(static document => document.Path));
        }

        var overlayUpdate = await _overlayIndex.Update(
            new StoredDocumentSource(load.Documents),
            CreateDeletionKeys(deletionSet),
            new UpdateOptions(options.RejectedUpsertPolicy),
            cancellationToken);

        var deletedDocuments = 0;
        _stateLock.EnterWriteLock();
        try
        {
            for (var i = 0; i < load.Documents.Count; i++)
            {
                var document = load.Documents[i];
                _overlayDocuments[document.Path] = document.MTimeUtcTicks;
                _deletedPaths.Remove(document.Path);
            }

            foreach (var path in deletionSet)
            {
                var removedOverlayDocument = _overlayDocuments.Remove(path);
                var baseDocumentExists = _baseSnapshot.ContainsDocument(new DocumentKey(path));
                var newlyHiddenBaseDocument = baseDocumentExists && _deletedPaths.Add(path);
                if (removedOverlayDocument || newlyHiddenBaseDocument)
                {
                    deletedDocuments++;
                }

                if (!baseDocumentExists)
                {
                    _deletedPaths.Remove(path);
                }
            }

            RefreshOverlaySnapshot();
            RefreshHiddenPathVisibility();
        }
        finally
        {
            _stateLock.ExitWriteLock();
        }

        await CompactOverlayIfNeeded(cancellationToken).ConfigureAwait(false);
        watch.Stop();

        return new UpdateStats(
            load.Submitted,
            overlayUpdate.UpsertedDocuments,
            overlayUpdate.SkippedUpserts,
            load.Rejected,
            deletedDocuments,
            overlayUpdate.PublishedSegments,
            load.SubmittedBytes,
            overlayUpdate.ContentBytes,
            overlayUpdate.SkippedBytes,
            load.RejectedBytes,
            load.RejectedDocuments,
            watch.ElapsedMilliseconds,
            overlayUpdate.CommittedIdentity);
    }

    public Task<UpdateStats> ApplyTo(ISift target)
    {
        return ApplyTo(target, new UpdateOptions(), CancellationToken.None);
    }

    public Task<UpdateStats> ApplyTo(ISift target, CancellationToken cancellationToken)
    {
        return ApplyTo(target, new UpdateOptions(), cancellationToken);
    }

    public Task<UpdateStats> ApplyTo(ISift target, UpdateOptions options)
    {
        return ApplyTo(target, options, CancellationToken.None);
    }

    public Task<UpdateStats> ApplyTo(
        ISift target,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        return ApplyToCore(target, options, cancellationToken);
    }

    public DocumentVisitStats VisitDocuments(IDocumentKeyCollector collector)
    {
        return VisitDocuments(collector, CancellationToken.None);
    }

    public DocumentVisitStats VisitDocuments(
        IDocumentKeyCollector collector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collector);
        using var state = BeginRead();
        var hiddenPaths = new HashSet<string>(_deletedPaths, StringComparer.Ordinal);
        hiddenPaths.UnionWith(_overlayDocuments.Keys);
        var visibleBaseCollector = new VisibleBaseDocumentCollector(hiddenPaths, collector);
        var baseStats = _baseSnapshot.VisitDocuments(visibleBaseCollector, cancellationToken);
        if (!baseStats.Completed)
        {
            return new DocumentVisitStats(visibleBaseCollector.VisitedDocuments, Completed: false);
        }

        var overlayStats = _overlaySnapshot.VisitDocuments(collector, cancellationToken);
        return new DocumentVisitStats(
            visibleBaseCollector.VisitedDocuments + overlayStats.VisitedDocuments,
            overlayStats.Completed);
    }

    public SearchPlan Explain(in SearchQuery query, in PathFilter pathFilter)
    {
        using var state = BeginRead();
        var basePlan = _baseSnapshot.Explain(query, pathFilter);
        if (_overlayDocuments.Count == 0 && _deletedPaths.Count == 0)
        {
            return basePlan;
        }

        var overlayPlan = _overlaySnapshot.Explain(query, pathFilter);
        var dominantPlan = basePlan.ScopeByteCount >= overlayPlan.ScopeByteCount ? basePlan : overlayPlan;

        return new SearchPlan(
            dominantPlan.PatternKind,
            dominantPlan.ResolvedCaseMode,
            basePlan.ScopeDocumentCount + overlayPlan.ScopeDocumentCount,
            basePlan.ScopeByteCount + overlayPlan.ScopeByteCount,
            MergeStrings(basePlan.MandatoryFragments, overlayPlan.MandatoryFragments),
            MergeStrings(basePlan.AdvisorySeeds, overlayPlan.AdvisorySeeds),
            MergeSelectedNgrams(basePlan.SelectedNgrams, overlayPlan.SelectedNgrams),
            dominantPlan.UsesPathPrefixFilter,
            dominantPlan.UsesPathRegexFilter,
            dominantPlan.UsesIncludeGlobs,
            dominantPlan.UsesExcludeGlobs,
            basePlan.UsesPathIndex || overlayPlan.UsesPathIndex,
            basePlan.UsesIndex || overlayPlan.UsesIndex,
            basePlan.CanUseLiteralVerifier && overlayPlan.CanUseLiteralVerifier,
            basePlan.CanUseNonBacktracking && overlayPlan.CanUseNonBacktracking,
            basePlan.NeedsUnicodeDecode || overlayPlan.NeedsUnicodeDecode,
            basePlan.MayRequireWholeDocumentVerification || overlayPlan.MayRequireWholeDocumentVerification,
            basePlan.EstimatedPathFilterCost + overlayPlan.EstimatedPathFilterCost,
            basePlan.EstimatedDirectScanCost + overlayPlan.EstimatedDirectScanCost,
            SumNullable(basePlan.EstimatedIndexedCost, overlayPlan.EstimatedIndexedCost),
            basePlan.EstimatedCandidateDocuments + overlayPlan.EstimatedCandidateDocuments,
            basePlan.EstimatedCost + overlayPlan.EstimatedCost,
            dominantPlan.CandidateKind,
            dominantPlan.CandidateCombination,
            dominantPlan.VerifierKind,
            dominantPlan.RunnerUp,
            dominantPlan.RunnerUpEstimatedCostDelta,
            dominantPlan.TimeoutPolicy,
            dominantPlan.PlannerAlternatives);
    }

    public SearchStats Search(in SearchQuery query, in PathFilter pathFilter, IHitCollector collector)
    {
        return Search(query, pathFilter, collector, CancellationToken.None);
    }

    public SearchStats Search(
        in SearchQuery query,
        in PathFilter pathFilter,
        IHitCollector collector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collector);
        using var state = BeginRead();
        var watch = Stopwatch.StartNew();

        var overlayCollector = new OverlayHitCollector(this, collector);
        var overlayStats = _overlaySnapshot.Search(query, pathFilter, overlayCollector, cancellationToken);
        if (overlayStats.StopReason != SearchStopReason.None)
        {
            return overlayStats;
        }

        var remainingHits = query.MaxHits - overlayStats.HitsReturned;
        var remainingDocuments = query.MaxDocuments - overlayCollector.HitDocuments;
        var remainingBytes = query.MaxBytesScanned - overlayStats.BytesScanned;
        if (remainingHits <= 0)
        {
            return overlayStats.WithStopReason(SearchStopReason.MaxHits);
        }

        if (remainingDocuments <= 0)
        {
            return overlayStats.WithStopReason(SearchStopReason.MaxDocuments);
        }

        if (remainingBytes <= 0)
        {
            return overlayStats.WithStopReason(SearchStopReason.MaxBytesScanned);
        }

        var remainingElapsedMilliseconds = query.MaxElapsedMilliseconds - watch.ElapsedMilliseconds;
        if (remainingElapsedMilliseconds <= 0)
        {
            return overlayStats.WithStopReason(SearchStopReason.MaxElapsedTime);
        }

        var baseCollector = new TrackingCollector(collector);
        var baseStats = _baseSnapshot.Search(
            query with
            {
                MaxHits = remainingHits,
                MaxDocuments = remainingDocuments,
                MaxBytesScanned = remainingBytes,
                MaxElapsedMilliseconds = remainingElapsedMilliseconds
            },
            pathFilter,
            baseCollector,
            _hiddenPathVisibility,
            cancellationToken);

        return CombineStats(overlayStats, baseStats);
    }

    public MaterializedHit Materialize(in RawHit hit, int contextBefore, int contextAfter)
    {
        using var state = BeginRead();
        ulong overlaySegmentId;
        lock (_syntheticSync)
        {
            if (!_syntheticToOverlaySegmentIds.TryGetValue(hit.SegmentId, out overlaySegmentId))
            {
                return _baseSnapshot.Materialize(hit, contextBefore, contextAfter);
            }
        }

        return _overlaySnapshot.Materialize(
            hit with { SegmentId = overlaySegmentId },
            contextBefore,
            contextAfter);
    }

    public bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content)
    {
        using var state = BeginRead();
        var normalizedPath = key.Path;
        if (_overlayDocuments.ContainsKey(normalizedPath))
        {
            return _overlaySnapshot.TryGetDocumentBytes(key, out content);
        }

        if (_deletedPaths.Contains(normalizedPath))
        {
            content = default;
            return false;
        }

        return _baseSnapshot.TryGetDocumentBytes(key, out content);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        _applyLock.Wait();
        try
        {
            _stateLock.EnterWriteLock();
            try
            {
                _overlaySnapshot.Dispose();
                _overlayIndex.Dispose();
                _baseSnapshot.Dispose();
            }
            finally
            {
                _stateLock.ExitWriteLock();
            }
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private void RefreshOverlaySnapshot()
    {
        _overlaySnapshot.Dispose();
        _overlaySnapshot = _overlayIndex.OpenSnapshot(_searchOptions);
        lock (_syntheticSync)
        {
            _overlayToSyntheticSegmentIds.Clear();
            _syntheticToOverlaySegmentIds.Clear();
            _nextSyntheticSegmentId = ulong.MaxValue;
        }
    }

    private ulong GetSyntheticSegmentId(ulong overlaySegmentId)
    {
        lock (_syntheticSync)
        {
            if (_overlayToSyntheticSegmentIds.TryGetValue(overlaySegmentId, out var syntheticSegmentId))
            {
                return syntheticSegmentId;
            }

            while (_baseSegmentIds.Contains(_nextSyntheticSegmentId) || _syntheticToOverlaySegmentIds.ContainsKey(_nextSyntheticSegmentId))
            {
                _nextSyntheticSegmentId--;
            }

            syntheticSegmentId = _nextSyntheticSegmentId--;
            _overlayToSyntheticSegmentIds.Add(overlaySegmentId, syntheticSegmentId);
            _syntheticToOverlaySegmentIds.Add(syntheticSegmentId, overlaySegmentId);
            return syntheticSegmentId;
        }
    }

    private StateReadLease BeginRead()
    {
        _stateLock.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            return new StateReadLease(_stateLock);
        }
        catch
        {
            _stateLock.ExitReadLock();
            throw;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private void RefreshHiddenPathVisibility()
    {
        var hidden = new byte[_overlayDocuments.Count + _deletedPaths.Count][];
        var index = 0;
        foreach (var path in _overlayDocuments.Keys)
        {
            hidden[index++] = Encoding.UTF8.GetBytes(path);
        }

        foreach (var path in _deletedPaths)
        {
            hidden[index++] = Encoding.UTF8.GetBytes(path);
        }

        Array.Sort(hidden, static (left, right) => left.AsSpan().SequenceCompareTo(right));
        _hiddenPathVisibility = new HiddenPathVisibility(hidden);
    }

    private List<OverlayDocumentMetadata> GetSortedOverlayDocumentMetadata()
    {
        var documents = new List<OverlayDocumentMetadata>(_overlayDocuments.Count);
        foreach (var pair in _overlayDocuments)
        {
            documents.Add(new OverlayDocumentMetadata(pair.Key, pair.Value));
        }

        documents.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        return documents;
    }

    private static SearchStats CombineStats(in SearchStats overlay, in SearchStats @base)
    {
        return new SearchStats(
            overlay.ElapsedMilliseconds + @base.ElapsedMilliseconds,
            overlay.ScopeDocuments + @base.ScopeDocuments,
            overlay.ScopeBytes + @base.ScopeBytes,
            overlay.PostingsBytesRead + @base.PostingsBytesRead,
            overlay.CandidateDocumentsAfterIndex + @base.CandidateDocumentsAfterIndex,
            overlay.CandidateDocumentsAfterPrecheck + @base.CandidateDocumentsAfterPrecheck,
            overlay.VerifiedDocuments + @base.VerifiedDocuments,
            overlay.BytesScanned + @base.BytesScanned,
            overlay.DecodedChars + @base.DecodedChars,
            overlay.RegexInvocations + @base.RegexInvocations,
            overlay.Utf8RegexInvocations + @base.Utf8RegexInvocations,
            overlay.FallbackRegexInvocations + @base.FallbackRegexInvocations,
            overlay.NonBacktrackingRegexInvocations + @base.NonBacktrackingRegexInvocations,
            overlay.Utf8RegexMatchedDocuments + @base.Utf8RegexMatchedDocuments,
            overlay.Utf8RegexMatchesEnumerated + @base.Utf8RegexMatchesEnumerated,
            overlay.Utf8RegexCacheHits + @base.Utf8RegexCacheHits,
            overlay.Utf8RegexCacheMisses + @base.Utf8RegexCacheMisses,
            overlay.HitsReturned + @base.HitsReturned,
            @base.StopReason);
    }

    private static string[] MergeStrings(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0)
        {
            return [.. right];
        }

        if (right.Count == 0)
        {
            return [.. left];
        }

        var merged = new List<string>(left.Count + right.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < left.Count; i++)
        {
            if (seen.Add(left[i]))
            {
                merged.Add(left[i]);
            }
        }

        for (var i = 0; i < right.Count; i++)
        {
            if (seen.Add(right[i]))
            {
                merged.Add(right[i]);
            }
        }

        return [.. merged];
    }

    private static SelectedNGram[] MergeSelectedNgrams(
        IReadOnlyList<SelectedNGram> left,
        IReadOnlyList<SelectedNGram> right)
    {
        if (left.Count == 0)
        {
            return [.. right];
        }

        if (right.Count == 0)
        {
            return [.. left];
        }

        var merged = new List<SelectedNGram>(left.Count + right.Count);
        var seen = new HashSet<(string Text, int Length)>();
        for (var i = 0; i < left.Count; i++)
        {
            var key = (left[i].Text, left[i].Length);
            if (seen.Add(key))
            {
                merged.Add(left[i]);
            }
        }

        for (var i = 0; i < right.Count; i++)
        {
            var key = (right[i].Text, right[i].Length);
            if (seen.Add(key))
            {
                merged.Add(right[i]);
            }
        }

        return [.. merged];
    }

    private static double? SumNullable(double? left, double? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left.Value + right.Value;
    }

    private static DocumentKey[] CreateDeletionKeys(IEnumerable<string> paths)
    {
        if (paths is ICollection<string> collection)
        {
            var keys = new DocumentKey[collection.Count];
            var index = 0;
            foreach (var path in collection)
            {
                keys[index++] = new DocumentKey(path);
            }

            return keys;
        }

        var list = new List<DocumentKey>();
        foreach (var path in paths)
        {
            list.Add(new DocumentKey(path));
        }

        return [.. list];
    }

    private sealed class StoredDocumentSource : IDocumentSource
    {
        private readonly IReadOnlyList<StoredDocument> _documents;
        private int _index;

        public StoredDocumentSource(IReadOnlyList<StoredDocument> documents)
        {
            _documents = documents;
        }

        public bool TryReadNext(out SubmittedDocument document)
        {
            if (_index >= _documents.Count)
            {
                document = default;
                return false;
            }

            var next = _documents[_index++];
            document = new SubmittedDocument(
                next.Path,
                next.Content,
                new DateTimeOffset(next.MTimeUtcTicks, TimeSpan.Zero),
                DocumentContentLifetime.UntilOperationCompletes);
            return true;
        }
    }

    private sealed class StateReadLease(ReaderWriterLockSlim stateLock) : IDisposable
    {
        private ReaderWriterLockSlim? _stateLock = stateLock;

        public void Dispose()
        {
            Interlocked.Exchange(ref _stateLock, null)?.ExitReadLock();
        }
    }

    private sealed class HiddenPathVisibility : IPathVisibility
    {
        public static HiddenPathVisibility Empty { get; } = new([]);

        private readonly byte[][] _hiddenPaths;

        public HiddenPathVisibility(byte[][] hiddenPaths)
        {
            _hiddenPaths = hiddenPaths;
        }

        public bool IsVisible(ReadOnlySpan<byte> normalizedPathUtf8)
        {
            var lower = 0;
            var upper = _hiddenPaths.Length - 1;
            while (lower <= upper)
            {
                var middle = lower + ((upper - lower) / 2);
                var comparison = normalizedPathUtf8.SequenceCompareTo(_hiddenPaths[middle]);
                if (comparison == 0)
                {
                    return false;
                }

                if (comparison < 0)
                {
                    upper = middle - 1;
                }
                else
                {
                    lower = middle + 1;
                }
            }

            return true;
        }
    }

    private sealed class VisibleBaseDocumentCollector(
        IReadOnlySet<string> hiddenPaths,
        IDocumentKeyCollector inner) : IDocumentKeyCollector
    {
        public int VisitedDocuments { get; private set; }

        public bool OnDocument(in DocumentKey key)
        {
            if (hiddenPaths.Contains(key.Path))
            {
                return true;
            }

            VisitedDocuments++;
            return inner.OnDocument(key);
        }
    }

    private readonly record struct OverlayDocumentMetadata(string Path, long MTimeUtcTicks);

    private sealed class SnapshotDocumentSource(
        IIndexSnapshot snapshot,
        IReadOnlyList<OverlayDocumentMetadata> documents) : IDocumentSource
    {
        private int _index;

        public bool TryReadNext(out SubmittedDocument document)
        {
            if (_index >= documents.Count)
            {
                document = default;
                return false;
            }

            var next = documents[_index++];
            if (!snapshot.TryGetDocumentBytes(new DocumentKey(next.Path), out var content))
            {
                throw new InvalidOperationException($"Overlay document '{next.Path}' disappeared while applying it.");
            }

            document = new SubmittedDocument(
                next.Path,
                content,
                new DateTimeOffset(next.MTimeUtcTicks, TimeSpan.Zero),
                DocumentContentLifetime.UntilOperationCompletes);
            return true;
        }
    }

    private async Task<UpdateStats> ApplyToCore(
        ISift target,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var documents = GetSortedOverlayDocumentMetadata();
            var deletions = CreateDeletionKeys(_deletedPaths);
            return await target.Update(
                new SnapshotDocumentSource(_overlaySnapshot, documents),
                deletions,
                options,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task CompactOverlayIfNeeded(CancellationToken cancellationToken)
    {
        var maintenance = _overlayIndex.GetMaintenanceStats();
        var minimumSegmentsForDocuments = maintenance.LiveDocuments == 0
            ? 0
            : (int)(((long)maintenance.LiveDocuments + _options.TargetSegmentDocumentCount - 1) /
              _options.TargetSegmentDocumentCount);
        var minimumSegmentsForContent = maintenance.EstimatedLiveContentBytes == 0
            ? 0L
            : (maintenance.EstimatedLiveContentBytes + _options.TargetSegmentContentBytes - 1) /
              _options.TargetSegmentContentBytes;
        var minimumSegments = Math.Max(
            minimumSegmentsForDocuments,
            (int)Math.Min(int.MaxValue, minimumSegmentsForContent));
        if (maintenance.SegmentCount - minimumSegments < _options.OverlayCompactionSegmentThreshold)
        {
            return;
        }

        await _overlayIndex.Compact(
            new CompactOptions
            {
                MaxMergedSegments = int.MaxValue,
                DeadFractionThreshold = 0,
                DeadContentFractionThreshold = 0,
                SmallSegmentMergeThreshold = 1,
                MetadataBloatDeadOrdinalsThreshold = 1,
                MaxInputSegmentBytes = long.MaxValue,
                MinimumFreeBytesAfterCompaction = 0
            },
            cancellationToken).ConfigureAwait(false);

        _stateLock.EnterWriteLock();
        try
        {
            RefreshOverlaySnapshot();
        }
        finally
        {
            _stateLock.ExitWriteLock();
        }
    }

    internal MaintenanceStats GetOverlayMaintenanceStats()
    {
        return _overlayIndex.GetMaintenanceStats();
    }

    internal int GetRetainedTombstoneCount()
    {
        using var state = BeginRead();
        return _deletedPaths.Count;
    }

    private sealed class TrackingCollector : IHitCollector
    {
        private readonly IHitCollector _inner;
        private readonly HashSet<(ulong SegmentId, uint DocumentOrdinal)> _documentsWithHits = [];

        public TrackingCollector(IHitCollector inner)
        {
            _inner = inner;
        }

        public int HitDocuments => _documentsWithHits.Count;

        public bool OnHit(in RawHit hit)
        {
            _documentsWithHits.Add((hit.SegmentId, hit.DocumentOrdinal));
            return _inner.OnHit(hit);
        }
    }

    private sealed class OverlayHitCollector : IHitCollector
    {
        private readonly OverlaySnapshot _owner;
        private readonly IHitCollector _inner;
        private readonly HashSet<(ulong SegmentId, uint DocumentOrdinal)> _documentsWithHits = [];

        public OverlayHitCollector(OverlaySnapshot owner, IHitCollector inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public int HitDocuments => _documentsWithHits.Count;

        public bool OnHit(in RawHit hit)
        {
            var syntheticSegmentId = _owner.GetSyntheticSegmentId(hit.SegmentId);
            _documentsWithHits.Add((syntheticSegmentId, hit.DocumentOrdinal));
            return _inner.OnHit(hit with { SegmentId = syntheticSegmentId });
        }
    }
}
