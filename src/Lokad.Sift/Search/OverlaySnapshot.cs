using System.Diagnostics;
using System.Text;
using Lokad.Sift;
using Lokad.Sift.Indexer;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

internal sealed class OverlaySnapshot : IOverlaySnapshot
{
    private readonly SearchSnapshot _baseSnapshot;
    private readonly IndexIdentity _baseIdentity;
    private readonly SiftIndex _overlayIndex;
    private readonly SiftOptions _options;
    private readonly SearchOptions _searchOptions;
    private readonly Dictionary<string, long> _overlayDocuments = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletedPaths = new(StringComparer.Ordinal);
    private readonly HashSet<SegmentId> _baseSegmentIds;
    private readonly Dictionary<SegmentId, SegmentId> _overlayToSyntheticSegmentIds = [];
    private readonly Dictionary<SegmentId, OverlayHitOrigin> _syntheticHitOrigins = [];
    private readonly List<IIndexSnapshot> _retainedOverlaySnapshots = [];
    // Collectors may materialize overlay hits recursively while Search holds a read lease.
    private readonly ReaderWriterLockSlim _stateLock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly object _syntheticSync = new();
    private OverlayGeneration _overlayGeneration;
    private HiddenPathVisibility _hiddenPathVisibility = HiddenPathVisibility.Empty;
    private ulong _nextSyntheticSegmentId = ulong.MaxValue;
    private ulong _revision;
    private int _disposed;

    internal OverlaySnapshot(SearchSnapshot baseSnapshot, SiftOptions options, SearchOptions searchOptions)
    {
        _baseSnapshot = baseSnapshot;
        _baseIdentity = baseSnapshot.Identity;
        _options = options;
        _searchOptions = searchOptions;
        _overlayIndex = SiftIndex.CreateInMemory(options);
        _overlayGeneration = new OverlayGeneration(_overlayIndex.OpenSnapshot(searchOptions));
        _baseSegmentIds = new HashSet<SegmentId>(_baseSnapshot.GetSegmentIds());
    }

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
                ThrowIfDisposed();
                await CompactOverlayIfNeeded(cancellationToken).ConfigureAwait(false);
                return await ApplyUpdateUnderLock().ConfigureAwait(false);
            }
            finally
            {
                _updateLock.Release();
            }
        }

        async Task<OverlayUpdateStats> ApplyUpdateUnderLock()
        {
            var watch = Stopwatch.StartNew();
            var load = IndexBuilder.LoadDocuments(
                upserts,
                cancellationToken,
                _options,
                options.RejectedUpsertPolicy,
                retainContentBeyondOperation: false);
            var deletionSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var deletion in deletions)
            {
                var normalized = deletion.Path;
                if (!deletionSet.Add(normalized))
                {
                    throw new InvalidOperationException($"Duplicate deletion path in overlay batch: '{normalized}'.");
                }
            }

            if (options.RejectedUpsertPolicy == RejectedUpsertPolicy.DeleteExisting)
            {
                deletionSet.UnionWith(load.RejectedDocuments.Select(static document => document.Path));
            }

            var overlayUpdate = await _overlayIndex.UpdatePrevalidated(
                load,
                CreateDeletionKeys(deletionSet),
                new UpdateOptions(options.RejectedUpsertPolicy),
                cancellationToken);

            var deletedDocuments = 0;
            OverlayRevision committedRevision;
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
                if (overlayUpdate.UpsertedDocuments > 0 || deletedDocuments > 0)
                {
                    _revision = checked(_revision + 1);
                }

                committedRevision = new OverlayRevision(_revision);
            }
            finally
            {
                _stateLock.ExitWriteLock();
            }

            watch.Stop();

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
        if (options.ExpectedIdentity is { } expectedIdentity &&
            (expectedIdentity.CorpusId != _baseIdentity.CorpusId ||
             expectedIdentity.ContentRevision != _baseIdentity.ContentRevision))
        {
            throw new ArgumentException(
                "ExpectedIdentity must identify the overlay's base revision.",
                nameof(options));
        }

        return ApplyDeltaToCore(
            baseIndex,
            options with { ExpectedIdentity = _baseIdentity },
            cancellationToken);
    }

    public Task<UpdateStats> ApplyDeltaTo(
        ISiftIndex target,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
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
        using var state = BeginRead();
        var hiddenPaths = new HashSet<string>(_deletedPaths, StringComparer.Ordinal);
        hiddenPaths.UnionWith(_overlayDocuments.Keys);
        var visibleBaseCollector = new VisibleBaseDocumentCollector(hiddenPaths, collector);
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
        using var state = BeginRead();
        var basePlan = _baseSnapshot.Explain(query, pathFilter);
        if (_overlayDocuments.Count == 0 && _deletedPaths.Count == 0)
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
        using var state = BeginRead();
        var watch = Stopwatch.StartNew();

        var generation = _overlayGeneration;
        var overlayCollector = new OverlayHitCollector(this, generation, collector);
        var overlayStats = generation.Snapshot.Search(query, pathFilter, overlayCollector, cancellationToken);
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
                MaxMatchedDocuments = remainingDocuments,
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
        OverlayHitOrigin origin;
        lock (_syntheticSync)
        {
            if (!_syntheticHitOrigins.TryGetValue(hit.SegmentId, out origin))
            {
                return _baseSnapshot.Materialize(hit, contextBefore, contextAfter);
            }
        }

        return origin.Snapshot.Materialize(
            hit.WithSegmentId(origin.SegmentId),
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
            var found = generation.Snapshot.TryGetDocumentBytes(key, out content);
            if (found)
            {
                generation.MarkEscaped();
            }

            return found;
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

        _updateLock.Wait();
        try
        {
            _stateLock.EnterWriteLock();
            try
            {
                _overlayGeneration.Snapshot.Dispose();
                foreach (var snapshot in _retainedOverlaySnapshots)
                {
                    snapshot.Dispose();
                }

                _retainedOverlaySnapshots.Clear();
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
    }

    private void RefreshOverlaySnapshot()
    {
        var next = new OverlayGeneration(_overlayIndex.OpenSnapshot(_searchOptions));
        var previous = _overlayGeneration;
        _overlayGeneration = next;
        if (previous.HasEscaped)
        {
            _retainedOverlaySnapshots.Add(previous.Snapshot);
        }
        else
        {
            previous.Snapshot.Dispose();
        }
    }

    private SegmentId GetSyntheticSegmentId(OverlayGeneration generation, SegmentId overlaySegmentId)
    {
        lock (_syntheticSync)
        {
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
            generation.MarkEscaped();
            _overlayToSyntheticSegmentIds.Add(overlaySegmentId, syntheticSegmentId);
            _syntheticHitOrigins.Add(
                syntheticSegmentId,
                new OverlayHitOrigin(generation.Snapshot, overlaySegmentId));
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

    private static SearchStats CombineStats(SearchStats overlay, SearchStats @base)
    {
        var metrics = SearchMetrics.FromSearchStats(overlay);
        metrics.Add(SearchMetrics.FromSearchStats(@base));
        return metrics.ToSearchStats(
            overlay.ElapsedMilliseconds + @base.ElapsedMilliseconds,
            overlay.HitsReturned + @base.HitsReturned,
            @base.StopReason);
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

        public bool OnDocument(DocumentKey key)
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

    private async Task<UpdateStats> ApplyDeltaToCore(
        ISiftIndex target,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var documents = GetSortedOverlayDocumentMetadata();
            var deletions = CreateDeletionKeys(_deletedPaths);
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
        // A generation whose results escaped must keep its retired packs readable.
        // Retaining those results takes precedence over compacting this transient
        // index; disposing the overlay releases the entire index at once.
        if (_retainedOverlaySnapshots.Count > 0)
        {
            return;
        }

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
        private readonly HashSet<(SegmentId SegmentId, DocumentOrdinal DocumentOrdinal)> _documentsWithHits = [];

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
        private readonly OverlayGeneration _generation;
        private readonly IHitCollector _inner;
        private readonly HashSet<(SegmentId SegmentId, DocumentOrdinal DocumentOrdinal)> _documentsWithHits = [];

        public OverlayHitCollector(
            OverlaySnapshot owner,
            OverlayGeneration generation,
            IHitCollector inner)
        {
            _owner = owner;
            _generation = generation;
            _inner = inner;
        }

        public int HitDocuments => _documentsWithHits.Count;

        public bool OnHit(in RawHit hit)
        {
            var syntheticSegmentId = _owner.GetSyntheticSegmentId(_generation, hit.SegmentId);
            _documentsWithHits.Add((syntheticSegmentId, hit.DocumentOrdinal));
            var syntheticHit = hit.WithSegmentId(syntheticSegmentId);
            return _inner.OnHit(syntheticHit);
        }
    }

    private sealed class OverlayGeneration(IIndexSnapshot snapshot)
    {
        private int _escaped;

        public IIndexSnapshot Snapshot { get; } = snapshot;
        public bool HasEscaped => Volatile.Read(ref _escaped) != 0;

        public void MarkEscaped() => Volatile.Write(ref _escaped, 1);
    }

    private readonly record struct OverlayHitOrigin(
        IIndexSnapshot Snapshot,
        SegmentId SegmentId);
}
