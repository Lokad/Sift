using System.Text;
using Lokad.Sift;
using Lokad.Sift.Indexer;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal sealed class OverlaySnapshot : IOverlaySnapshot
{
    private const int BaseExistenceCacheCapacity = 4096;
    private const int BaseExistenceCacheCharacterCapacity = 256 * 1024;
    private readonly SearchSnapshot _baseSnapshot;
    private readonly IndexIdentity _baseIdentity;
    private readonly SiftIndex _overlayIndex;
    private readonly SiftOptions _options;
    private readonly SearchOptions _searchOptions;
    private SortedDictionary<string, long> _overlayDocuments = new(StringComparer.Ordinal);
    private HashSet<string> _baseTombstones = new(StringComparer.Ordinal);
    private readonly BoundedLruCache<string, bool> _baseDocumentExistence = new(
        BaseExistenceCacheCapacity,
        BaseExistenceCacheCharacterCapacity,
        static path => path.Length);
    private readonly HashSet<SegmentId> _baseSegmentIds;
    private readonly Dictionary<SegmentId, SegmentId> _overlayToSyntheticSegmentIds = [];
    private readonly Dictionary<SegmentId, OverlayHitOrigin> _syntheticHitOrigins = [];
    private readonly object _hitOwner = new();
    // Collectors may materialize overlay hits recursively while Search holds a read lease.
    private readonly ReaderWriterLockSlim _stateLock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly object _syntheticSync = new();
    private OverlayGeneration _overlayGeneration;
    private HiddenPathVisibility _hiddenPathVisibility = HiddenPathVisibility.Empty;
    private ulong _nextSyntheticSegmentId = ulong.MaxValue;
    private ulong _revision;
    private Exception? _failure;
    private int _disposed;

    internal OverlaySnapshot(SearchSnapshot baseSnapshot, SiftOptions options, SearchOptions searchOptions)
    {
        _baseSnapshot = baseSnapshot;
        _baseIdentity = baseSnapshot.Identity;
        _options = options;
        _searchOptions = searchOptions;
        var overlayIndex = new SiftIndex(new InMemorySiftStorage(), options, baseSnapshot.RegexContext);
        SearchSnapshot? overlaySnapshot = null;
        try
        {
            overlaySnapshot = (SearchSnapshot)overlayIndex.OpenSnapshot(searchOptions);
            var overlayGeneration = new OverlayGeneration(overlaySnapshot);
            var baseSegmentIds = new HashSet<SegmentId>(_baseSnapshot.GetSegmentIds());
            _overlayIndex = overlayIndex;
            _overlayGeneration = overlayGeneration;
            _baseSegmentIds = baseSegmentIds;
        }
        catch
        {
            // Ownership of the base snapshot transfers at entry, so a failed constructor
            // must release every staged resource that the caller can no longer receive.
            overlaySnapshot?.Dispose();
            overlayIndex.Dispose();
            _baseSnapshot.Dispose();
            throw;
        }
    }

    public IndexIdentity Identity => _baseIdentity;
    public ReadOnlyMemory<byte> CommitMetadata => _baseSnapshot.CommitMetadata;

    public Task<OverlayUpdateStats> Update(IDocumentSource upserts, IReadOnlyList<DocumentKey> deletions)
    {
        return Update(
            upserts,
            deletions,
            new OverlayUpdateOptions(_options.RejectedUpsertPolicy),
            CancellationToken.None);
    }

    public Task<OverlayUpdateStats> Update(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        CancellationToken cancellationToken)
    {
        return Update(
            upserts,
            deletions,
            new OverlayUpdateOptions(_options.RejectedUpsertPolicy),
            cancellationToken);
    }

    public Task<OverlayUpdateStats> Update(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayUpdateOptions options)
    {
        return Update(upserts, deletions, options, CancellationToken.None);
    }

    public Task<OverlayUpdateStats> Update(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
        OverlayUpdateOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(deletions);
        ArgumentNullException.ThrowIfNull(options);
        if (ReentrancyGuard.IsActive(this))
        {
            throw new InvalidOperationException(
                "An overlay cannot be updated from one of its active callbacks.");
        }

        if (!Enum.IsDefined(options.RejectedUpsertPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RejectedUpsertPolicy is invalid.");
        }

        return UpdateAsync();

        async Task<OverlayUpdateStats> UpdateAsync()
        {
            await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfUnavailable();
                // Document sources are caller callbacks. Mark the serialized mutation so
                // synchronous reentry fails instead of waiting on the lock already held here.
                using var callbackScope = ReentrancyGuard.Enter(this);
                await CompactOverlayIfNeeded(cancellationToken).ConfigureAwait(false);
                return await ApplyUpdateUnderLock().ConfigureAwait(false);
            }
            catch (MutationCallbackFailureException exception)
            {
                exception.DispatchInfo.Throw();
                throw;
            }
            finally
            {
                _updateLock.Release();
            }
        }

        async Task<OverlayUpdateStats> ApplyUpdateUnderLock()
        {
            var watch = ValueStopwatch.StartNew();
            var load = IndexBuilder.LoadDocuments(
                upserts,
                cancellationToken,
                _options,
                operationName: "overlay update",
                options.RejectedUpsertPolicy);
            var deletionSet = MutationCallbackGuards.ReadDeletionPaths(deletions, cancellationToken, "overlay");

            if (options.RejectedUpsertPolicy == RejectedUpsertPolicy.DeleteExisting)
            {
                deletionSet.UnionWith(load.RejectedDocuments.Select(static document => document.Path));
            }

            for (var i = 0; i < load.Documents.Count; i++)
            {
                if (deletionSet.Contains(load.Documents[i].Path))
                {
                    throw new InvalidOperationException(
                        $"Path '{load.Documents[i].Path}' cannot appear in both upserts and deletions.");
                }
            }

            var baseDocuments = new Dictionary<string, bool>(deletionSet.Count, StringComparer.Ordinal);
            foreach (var path in deletionSet)
            {
                var exists = _baseDocumentExistence.GetOrAdd(
                    path,
                    key => _baseSnapshot.ContainsDocument(new DocumentKey(key)),
                    out _);
                baseDocuments.Add(path, exists);
            }

            SortedDictionary<string, long> nextOverlayDocuments;
            HashSet<string> nextBaseTombstones;
            IReadOnlyDictionary<string, long> currentOverlayDocuments;
            IReadOnlySet<string> currentBaseTombstones;
            HiddenPathVisibility currentVisibility;
            ulong currentRevision;
            var deletedDocuments = 0;
            _stateLock.EnterReadLock();
            try
            {
                currentOverlayDocuments = _overlayDocuments;
                currentBaseTombstones = _baseTombstones;
                nextOverlayDocuments = new SortedDictionary<string, long>(
                    _overlayDocuments,
                    StringComparer.Ordinal);
                nextBaseTombstones = new HashSet<string>(_baseTombstones, StringComparer.Ordinal);
                currentVisibility = _hiddenPathVisibility;
                currentRevision = _revision;
            }
            finally
            {
                _stateLock.ExitReadLock();
            }

            for (var i = 0; i < load.Documents.Count; i++)
            {
                var document = load.Documents[i];
                nextOverlayDocuments[document.Path] = document.MTimeUtcTicks;
                nextBaseTombstones.Remove(document.Path);
            }

            foreach (var path in deletionSet)
            {
                var removedOverlayDocument = nextOverlayDocuments.Remove(path);
                var baseDocumentExists = baseDocuments[path];
                var newlyHiddenBaseDocument = baseDocumentExists && nextBaseTombstones.Add(path);
                if (removedOverlayDocument || newlyHiddenBaseDocument)
                {
                    deletedDocuments++;
                }

                if (!baseDocumentExists)
                {
                    nextBaseTombstones.Remove(path);
                }
            }

            var incrementedRevision = load.Documents.Count > 0 || deletedDocuments > 0
                ? checked(currentRevision + 1)
                : currentRevision;

            var visibilityAdditions = new List<byte[]>();
            var visibilityRemovals = new List<byte[]>();
            for (var i = 0; i < load.Documents.Count; i++)
            {
                var path = load.Documents[i].Path;
                if (!currentOverlayDocuments.ContainsKey(path) && !currentBaseTombstones.Contains(path))
                {
                    visibilityAdditions.Add(Encoding.UTF8.GetBytes(path));
                }
            }

            foreach (var path in deletionSet)
            {
                var shouldHide = nextOverlayDocuments.ContainsKey(path) || nextBaseTombstones.Contains(path);
                var isHidden = currentOverlayDocuments.ContainsKey(path) || currentBaseTombstones.Contains(path);
                if (shouldHide && !isHidden)
                {
                    visibilityAdditions.Add(Encoding.UTF8.GetBytes(path));
                }
                else if (!shouldHide && isHidden)
                {
                    visibilityRemovals.Add(Encoding.UTF8.GetBytes(path));
                }
            }

            var nextVisibility = currentVisibility.Apply(
                visibilityAdditions,
                visibilityRemovals);

            var overlayUpdate = await _overlayIndex.UpdatePrevalidated(
                load,
                CreateDeletionKeys(deletionSet),
                new UpdateOptions(options.RejectedUpsertPolicy),
                cancellationToken);
            var nextRevision = overlayUpdate.UpsertedDocuments > 0 || deletedDocuments > 0
                ? incrementedRevision
                : currentRevision;

            OverlayGeneration nextGeneration;
            try
            {
#if SIFT_INTERNAL_VERIFICATION
                BeforeOpeningCommittedSnapshot?.Invoke();
#endif
                nextGeneration = new OverlayGeneration(
                    (SearchSnapshot)_overlayIndex.OpenSnapshot(_searchOptions));
            }
            catch (Exception exception)
            {
                // The inner manifest is already committed. Keeping the old metadata usable
                // would let a later mutation combine two different logical generations.
                Interlocked.CompareExchange(ref _failure, exception, null);
                throw;
            }
            OverlayRevision committedRevision;
            OverlayGeneration previousGeneration;
            _stateLock.EnterWriteLock();
            try
            {
                previousGeneration = _overlayGeneration;
                InvalidateOverlayHits();
                _overlayGeneration = nextGeneration;
                _overlayDocuments = nextOverlayDocuments;
                _baseTombstones = nextBaseTombstones;
                _hiddenPathVisibility = nextVisibility;
                _revision = nextRevision;
                committedRevision = new OverlayRevision(nextRevision);
            }
            finally
            {
                _stateLock.ExitWriteLock();
            }
            previousGeneration.Snapshot.Dispose();

            var pendingStats = new PendingOverlayUpdateStats(
                submittedUpserts: load.Submitted,
                upsertedDocuments: overlayUpdate.UpsertedDocuments,
                skippedUpserts: overlayUpdate.SkippedUpserts,
                rejectedUpserts: load.Rejected,
                deletedDocuments: deletedDocuments,
                publishedSegments: overlayUpdate.PublishedSegments,
                submittedBytes: load.SubmittedBytes,
                contentBytes: overlayUpdate.ContentBytes,
                skippedBytes: overlayUpdate.SkippedBytes,
                rejectedBytes: load.RejectedBytes,
                rejectedDocumentDetails: load.RejectedDocuments.ToArray());
            return new OverlayUpdateStats(
                pendingStats,
                watch.ElapsedMilliseconds,
                committedRevision);
        }
    }

    private sealed class PendingOverlayUpdateStats(
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
        RejectedDocument[] rejectedDocumentDetails)
        : UpdateCommitStats(
            submittedUpserts,
            upsertedDocuments,
            skippedUpserts,
            rejectedUpserts,
            deletedDocuments,
            publishedSegments,
            submittedBytes,
            contentBytes,
            skippedBytes,
            rejectedBytes,
            rejectedDocumentDetails);

    public Task<UpdateStats> CommitToBase(
        ISiftIndex baseIndex,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseIndex);
        ArgumentNullException.ThrowIfNull(options);
        var baseRevision = _baseIdentity.Revision;
        if (options.ExpectedIdentity is { } expectedIdentity &&
            expectedIdentity != _baseIdentity)
        {
            throw new ArgumentException(
                "ExpectedIdentity must identify the overlay's base manifest.",
                nameof(options));
        }
        if (options.ExpectedRevision is { } expectedRevision &&
            expectedRevision != baseRevision)
        {
            throw new ArgumentException(
                "ExpectedRevision must identify the overlay's base revision.",
                nameof(options));
        }

        return ApplyDeltaToCore(
            baseIndex,
            options with { ExpectedRevision = baseRevision },
            cancellationToken);
    }

    public Task<UpdateStats> ApplyDeltaTo(
        ISiftIndex target,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        return ApplyDeltaToCore(target, options, cancellationToken);
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
        using var callbackScope = ReentrancyGuard.Enter(this);
        using var state = BeginRead();
        var visibleBaseCollector = new VisibleBaseDocumentCollector(
            _baseTombstones,
            _overlayDocuments,
            collector);
        var baseStats = _baseSnapshot.VisitDocuments(visibleBaseCollector, cancellationToken);
        if (!baseStats.Completed)
        {
            return new DocumentVisitStats(visibleBaseCollector.VisitedDocuments, Completed: false);
        }

        var overlayStats = _overlayGeneration.Snapshot.VisitDocuments(collector, cancellationToken);
        return new DocumentVisitStats(
            visibleBaseCollector.VisitedDocuments + overlayStats.VisitedDocuments,
            overlayStats.Completed);
    }

    public SearchPlan Explain(SearchQuery query, PathFilter pathFilter)
    {
        using var callbackScope = ReentrancyGuard.Enter(this);
        using var state = BeginRead();
        var basePlan = _baseSnapshot.Explain(query, pathFilter);
        if (_overlayDocuments.Count == 0 && _baseTombstones.Count == 0)
        {
            return basePlan;
        }

        var overlayPlan = _overlayGeneration.Snapshot.Explain(query, pathFilter);
        return SearchPlanCombiner.Combine(basePlan, overlayPlan);
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
        ArgumentNullException.ThrowIfNull(collector);
        using var callbackScope = ReentrancyGuard.Enter(this);
        using var state = BeginRead();
        var watch = ValueStopwatch.StartNew();
        // Overlay and base are two generations of one logical search: prepare once and
        // enforce one absolute deadline while carrying the remaining result budgets.
        // Validate before constructing the deadline, including null/invalid queries.
        _baseSnapshot.ValidateSearchArguments(query, pathFilter);
        using var deadlineCancellation = SearchSnapshot.CreateDeadlineCancellation(
            query.MaxElapsedMilliseconds,
            cancellationToken);
        var effectiveCancellationToken = deadlineCancellation?.Token ?? cancellationToken;
        PreparedSearch prepared;
        try { prepared = _baseSnapshot.PrepareValidatedSearch(query, pathFilter, effectiveCancellationToken); }
        catch (RegexEngineFailureException exception)
        {
            exception.DispatchInfo.Throw();
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (effectiveCancellationToken.IsCancellationRequested)
        {
            return SearchSnapshot.PreparationStopped(watch, SearchStopReason.MaxElapsedTime);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) when (query.RegexTimeoutPolicy == RegexTimeoutPolicy.StopSearch)
        {
            return SearchSnapshot.PreparationStopped(watch, SearchStopReason.RegexTimeout);
        }
        using var preparedLease = prepared;

        var generation = _overlayGeneration;
        var overlayCollector = new OverlayHitCollector(this, generation, collector);
        var overlayStats = generation.Snapshot.SearchPrepared(
            query,
            prepared,
            overlayCollector,
            pathVisibility: null,
            SearchLimits.FromQuery(query),
            effectiveCancellationToken,
            cancellationToken,
            watch);
        if (overlayStats.StopReason != SearchStopReason.None)
        {
            return overlayStats;
        }

        var remainingHits = query.MaxHits - overlayStats.HitsReturned;
        var remainingDocuments = query.MaxMatchedDocuments - overlayCollector.HitDocuments;
        var remainingBytes = query.MaxBytesScanned - overlayStats.BytesScanned;
        if (remainingHits <= 0)
        {
            return overlayStats.WithStopReason(SearchStopReason.MaxHits);
        }

        if (remainingDocuments <= 0)
        {
            return overlayStats.WithStopReason(SearchStopReason.MaxMatchedDocuments);
        }

        if (remainingBytes <= 0)
        {
            return overlayStats.WithStopReason(SearchStopReason.MaxBytesScanned);
        }

        if (watch.ElapsedMilliseconds >= query.MaxElapsedMilliseconds)
        {
            return overlayStats.WithStopReason(SearchStopReason.MaxElapsedTime);
        }

        var baseStats = _baseSnapshot.SearchPrepared(
            query,
            // Both generations share one cache lookup, already counted by overlayStats.
            prepared with { Utf8RegexCacheHit = null },
            collector,
            _hiddenPathVisibility,
            new SearchLimits(remainingHits, remainingDocuments, remainingBytes),
            effectiveCancellationToken,
            cancellationToken,
            watch);

        return CombineStats(overlayStats, baseStats, watch.ElapsedMilliseconds);

        static SearchStats CombineStats(SearchStats overlay, SearchStats @base, long elapsedMilliseconds)
        {
            var metrics = SearchMetrics.FromSearchStats(overlay);
            metrics.Add(SearchMetrics.FromSearchStats(@base));
            return metrics.ToSearchStats(
                elapsedMilliseconds,
                overlay.HitsReturned + @base.HitsReturned,
                @base.StopReason);
        }
    }

    public MaterializedHit Materialize(in RawHit hit, int contextBefore, int contextAfter)
    {
        using var state = BeginRead();
        OverlayHitOrigin? origin;
        lock (_syntheticSync)
        {
            if (!_syntheticHitOrigins.TryGetValue(hit.SegmentId, out origin))
            {
                if (hit.IsOwnedBy(_hitOwner))
                {
                    throw new ArgumentException(
                        "The overlay hit is no longer valid because the overlay was updated.",
                        nameof(hit));
                }

                return _baseSnapshot.Materialize(hit, contextBefore, contextAfter);
            }

            if (!hit.IsOwnedBy(_hitOwner))
            {
                throw new ArgumentException("The hit was not produced by this snapshot.", nameof(hit));
            }
        }

        return origin.Snapshot.MaterializeOverlayHit(
            hit,
            origin.SegmentId,
            contextBefore,
            contextAfter);
    }

    public MaterializedHitWithLine MaterializeWithMatchingLine(
        in RawHit hit,
        int contextBefore,
        int contextAfter)
    {
        using var state = BeginRead();
        OverlayHitOrigin? origin;
        lock (_syntheticSync)
        {
            if (!_syntheticHitOrigins.TryGetValue(hit.SegmentId, out origin))
            {
                if (hit.IsOwnedBy(_hitOwner))
                {
                    throw new ArgumentException(
                        "The overlay hit is no longer valid because the overlay was updated.",
                        nameof(hit));
                }

                return _baseSnapshot.MaterializeWithMatchingLine(hit, contextBefore, contextAfter);
            }

            if (!hit.IsOwnedBy(_hitOwner))
                throw new ArgumentException("The hit was not produced by this snapshot.", nameof(hit));
        }

        return origin.Snapshot.MaterializeOverlayHitWithMatchingLine(
            hit,
            origin.SegmentId,
            contextBefore,
            contextAfter);
    }

    public bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content)
    {
        using var state = BeginRead();
        var normalizedPath = key.Path;
        if (_overlayDocuments.ContainsKey(normalizedPath))
        {
            var generation = _overlayGeneration;
            return generation.Snapshot.TryGetSnapshotIndependentDocumentBytes(key, out content);
        }

        if (_baseTombstones.Contains(normalizedPath))
        {
            content = default;
            return false;
        }

        return _baseSnapshot.TryGetDocumentBytes(key, out content);
    }

    public void Dispose()
    {
        if (ReentrancyGuard.IsActive(this))
        {
            throw new InvalidOperationException(
                "An overlay cannot be disposed from one of its active callbacks.");
        }

        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        _updateLock.Wait();
        try
        {
            _stateLock.EnterWriteLock();
            try
            {
                _overlayGeneration.Snapshot.Dispose();
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
            _updateLock.Release();
        }

        // Pre-existing waiters still acquire these primitives to observe _disposed and exit.
        // Disposing either primitive here would race their cleanup paths.
    }

    private void RefreshOverlaySnapshot()
    {
        var next = new OverlayGeneration(
            (SearchSnapshot)_overlayIndex.OpenSnapshot(_searchOptions));
        var previous = _overlayGeneration;
        InvalidateOverlayHits();
        _overlayGeneration = next;
        previous.Snapshot.Dispose();
    }

    private void InvalidateOverlayHits()
    {
        lock (_syntheticSync)
        {
            _overlayToSyntheticSegmentIds.Clear();
            _syntheticHitOrigins.Clear();
        }
    }

    private SegmentId CaptureHit(OverlayGeneration generation, in RawHit hit)
    {
        lock (_syntheticSync)
        {
            var overlaySegmentId = hit.SegmentId;
            if (_overlayToSyntheticSegmentIds.TryGetValue(overlaySegmentId, out var syntheticSegmentId))
            {
                return syntheticSegmentId;
            }

            syntheticSegmentId = new SegmentId(_nextSyntheticSegmentId);
            while (_baseSegmentIds.Contains(syntheticSegmentId) || _syntheticHitOrigins.ContainsKey(syntheticSegmentId))
            {
                syntheticSegmentId = new SegmentId(--_nextSyntheticSegmentId);
            }

            _nextSyntheticSegmentId--;
            _overlayToSyntheticSegmentIds.Add(overlaySegmentId, syntheticSegmentId);
            var origin = new OverlayHitOrigin(generation.Snapshot, overlaySegmentId);
            _syntheticHitOrigins.Add(syntheticSegmentId, origin);
            return syntheticSegmentId;
        }
    }

#if SIFT_INTERNAL_VERIFICATION
    internal Action? BeforeOpeningCommittedSnapshot { get; set; }

    internal int RetainedOverlayHitOriginCount
    {
        get
        {
            lock (_syntheticSync)
            {
                return _syntheticHitOrigins.Count;
            }
        }
    }
#endif

    private ReadLockLease BeginRead()
    {
        _stateLock.EnterReadLock();
        try
        {
            ThrowIfUnavailable();
            return new ReadLockLease(_stateLock);
        }
        catch
        {
            _stateLock.ExitReadLock();
            throw;
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (Volatile.Read(ref _failure) is { } failure)
        {
            throw new InvalidOperationException(
                "The overlay cannot be used after its committed state failed to open.",
                failure);
        }
    }

    private List<OverlayDocumentMetadata> GetSortedOverlayDocumentMetadata()
    {
        var documents = new List<OverlayDocumentMetadata>(_overlayDocuments.Count);
        foreach (var pair in _overlayDocuments)
        {
            documents.Add(new OverlayDocumentMetadata(pair.Key, pair.Value));
        }

        return documents;
    }

    private static DocumentKey[] CreateDeletionKeys(IReadOnlyCollection<string> paths)
    {
        var keys = new DocumentKey[paths.Count];
        var index = 0;
        foreach (var path in paths)
        {
            keys[index++] = new DocumentKey(path);
        }

        return keys;
    }

    private sealed class HiddenPathVisibility : IPathVisibility
    {
        private static readonly IComparer<byte[]> Utf8PathOrder =
            Comparer<byte[]>.Create(static (left, right) =>
                left.AsSpan().SequenceCompareTo(right));
        private readonly byte[][] _hiddenPaths;

        private HiddenPathVisibility(byte[][] hiddenPaths)
        {
            _hiddenPaths = hiddenPaths;
        }

        public static HiddenPathVisibility Empty { get; } = new([]);

        public HiddenPathVisibility Apply(List<byte[]> additions, List<byte[]> removals)
        {
            if (additions.Count == 0 && removals.Count == 0)
            {
                return this;
            }

            additions.Sort(Utf8PathOrder);
            removals.Sort(Utf8PathOrder);
            var merged = GC.AllocateUninitializedArray<byte[]>(
                _hiddenPaths.Length - removals.Count + additions.Count);
            var sourceIndex = 0;
            var removalIndex = 0;
            var additionIndex = 0;
            var writeIndex = 0;
            while (sourceIndex < _hiddenPaths.Length || additionIndex < additions.Count)
            {
                while (sourceIndex < _hiddenPaths.Length && removalIndex < removals.Count)
                {
                    var comparison = Utf8PathOrder.Compare(
                        _hiddenPaths[sourceIndex],
                        removals[removalIndex]);
                    if (comparison < 0)
                    {
                        break;
                    }

                    if (comparison == 0)
                    {
                        sourceIndex++;
                        removalIndex++;
                        continue;
                    }

                    throw new InvalidOperationException("The overlay visibility index is inconsistent.");
                }

                if (sourceIndex >= _hiddenPaths.Length)
                {
                    while (additionIndex < additions.Count)
                    {
                        merged[writeIndex++] = additions[additionIndex++];
                    }

                    break;
                }

                if (additionIndex >= additions.Count)
                {
                    merged[writeIndex++] = _hiddenPaths[sourceIndex++];
                    continue;
                }

                var additionComparison = Utf8PathOrder.Compare(
                    _hiddenPaths[sourceIndex],
                    additions[additionIndex]);
                if (additionComparison < 0)
                {
                    merged[writeIndex++] = _hiddenPaths[sourceIndex++];
                }
                else if (additionComparison > 0)
                {
                    merged[writeIndex++] = additions[additionIndex++];
                }
                else
                {
                    throw new InvalidOperationException("The overlay visibility index is inconsistent.");
                }
            }

            if (removalIndex != removals.Count ||
                additionIndex != additions.Count ||
                writeIndex != merged.Length)
            {
                throw new InvalidOperationException("The overlay visibility index is inconsistent.");
            }

            // Apply both deltas directly into the immutable next generation; an intermediate
            // retained array would double peak reference storage for mixed batches.
            return new HiddenPathVisibility(merged);
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
        IReadOnlySet<string> baseTombstones,
        IReadOnlyDictionary<string, long> overlayDocuments,
        IDocumentKeyCollector inner) : IDocumentKeyCollector
    {
        public int VisitedDocuments { get; private set; }

        public bool OnDocument(DocumentKey key)
        {
            if (baseTombstones.Contains(key.Path) || overlayDocuments.ContainsKey(key.Path))
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

    private async Task<UpdateStats> ApplyDeltaToCore(
        ISiftIndex target,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        if (ReentrancyGuard.IsActive(this))
        {
            throw new InvalidOperationException(
                "An overlay cannot be exported or committed from one of its active callbacks.");
        }

        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var documents = GetSortedOverlayDocumentMetadata();
            var deletions = CreateDeletionKeys(_baseTombstones);
            using var callbackScope = ReentrancyGuard.Enter(this);
            return await target.Update(
                new SnapshotDocumentSource(_overlayGeneration.Snapshot, documents),
                deletions,
                options,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _updateLock.Release();
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
                MinimumSegmentCountForSmallMerge = 1,
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

#if SIFT_INTERNAL_VERIFICATION
    internal MaintenanceStats GetOverlayMaintenanceStats()
    {
        return _overlayIndex.GetMaintenanceStats();
    }

    internal int GetRetainedTombstoneCount()
    {
        using var state = BeginRead();
        return _baseTombstones.Count;
    }
#endif

    private sealed class OverlayHitCollector : IHitCollector
    {
        private readonly OverlaySnapshot _owner;
        private readonly OverlayGeneration _generation;
        private readonly IHitCollector _inner;
        private SegmentId _sourceSegmentId;
        private SegmentId _syntheticSegmentId;
        private DocumentOrdinal _lastDocumentOrdinal;
        private int _hitDocuments;
        private bool _hasSourceSegment;
        private bool _hasHitDocument;

        public OverlayHitCollector(
            OverlaySnapshot owner,
            OverlayGeneration generation,
            IHitCollector inner)
        {
            _owner = owner;
            _generation = generation;
            _inner = inner;
        }

        public int HitDocuments => _hitDocuments;

        public bool OnHit(in RawHit hit)
        {
            if (!_hasSourceSegment || hit.SegmentId != _sourceSegmentId)
            {
                _sourceSegmentId = hit.SegmentId;
                _syntheticSegmentId = _owner.CaptureHit(_generation, hit);
                _hasSourceSegment = true;
                _hasHitDocument = false;
            }

            // Segment search emits every hit for a document contiguously.
            if (!_hasHitDocument || hit.DocumentOrdinal != _lastDocumentOrdinal)
            {
                _lastDocumentOrdinal = hit.DocumentOrdinal;
                _hitDocuments++;
                _hasHitDocument = true;
            }

            var syntheticHit = hit
                .WithSegmentId(_syntheticSegmentId)
                .WithOwner(_owner._hitOwner);
            return _inner.OnHit(syntheticHit);
        }
    }

    private sealed class OverlayGeneration(SearchSnapshot snapshot)
    {
        public SearchSnapshot Snapshot { get; } = snapshot;
    }

    private sealed class OverlayHitOrigin(SearchSnapshot snapshot, SegmentId segmentId)
    {
        public SearchSnapshot Snapshot { get; } = snapshot;
        public SegmentId SegmentId { get; } = segmentId;
    }
}
