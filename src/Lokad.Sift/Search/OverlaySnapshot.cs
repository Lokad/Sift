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
    private readonly Dictionary<string, StoredDocument> _overlayDocuments = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletedPaths = new(StringComparer.Ordinal);
    private readonly HashSet<ulong> _baseSegmentIds;
    private readonly Dictionary<ulong, ulong> _overlayToSyntheticSegmentIds = [];
    private readonly Dictionary<ulong, ulong> _syntheticToOverlaySegmentIds = [];
    // Collectors may materialize overlay hits recursively while Search holds a read lease.
    private readonly ReaderWriterLockSlim _stateLock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly object _syntheticSync = new();
    private IIndexSnapshot _overlaySnapshot;
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

    public Task<UpdateStats> Apply(IDocumentSource upserts, ReadOnlySpan<DocumentKey> deletions)
    {
        return Apply(upserts, deletions, CancellationToken.None);
    }

    public Task<UpdateStats> Apply(
        IDocumentSource upserts,
        ReadOnlySpan<DocumentKey> deletions,
        CancellationToken cancellationToken)
    {
        var deletionArray = deletions.ToArray();
        return ApplyCore(upserts, deletionArray, cancellationToken);
    }

    private async Task<UpdateStats> ApplyCore(IDocumentSource upserts, IReadOnlyCollection<DocumentKey> deletions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await ApplyCoreUnderLock(upserts, deletions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task<UpdateStats> ApplyCoreUnderLock(
        IDocumentSource upserts,
        IReadOnlyCollection<DocumentKey> deletions,
        CancellationToken cancellationToken)
    {
        var load = IndexBuilder.LoadDocuments(upserts, cancellationToken, _options);
        Sift.ThrowIfRejectedUpserts(
            _options.RejectedUpsertPolicy,
            load.Rejected,
            load.RejectedBytes);
        var deletionSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deletion in deletions)
        {
            var normalized = IndexBuilder.CanonicalizePath(deletion.Path);
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

        var overlayUpdate = await _overlayIndex.Update(
            new StoredDocumentSource(load.Documents),
            CreateDeletionKeys(deletionSet),
            cancellationToken);

        _stateLock.EnterWriteLock();
        try
        {
            for (var i = 0; i < load.Documents.Count; i++)
            {
                var document = load.Documents[i];
                _overlayDocuments[document.Path] = document;
                _deletedPaths.Remove(document.Path);
            }

            foreach (var path in deletionSet)
            {
                _overlayDocuments.Remove(path);
                _deletedPaths.Add(path);
            }

            RefreshOverlaySnapshot();
        }
        finally
        {
            _stateLock.ExitWriteLock();
        }

        return new UpdateStats(
            load.Submitted,
            overlayUpdate.UpsertedDocuments,
            overlayUpdate.SkippedUpserts,
            load.Rejected,
            deletionSet.Count,
            overlayUpdate.PublishedSegments,
            load.SubmittedBytes,
            overlayUpdate.ContentBytes,
            overlayUpdate.SkippedBytes,
            load.RejectedBytes,
            overlayUpdate.ElapsedMilliseconds);
    }

    public Task<UpdateStats> ApplyTo(ISift target)
    {
        return ApplyTo(target, CancellationToken.None);
    }

    public Task<UpdateStats> ApplyTo(ISift target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var state = BeginRead();
        return target.Update(
            new StoredDocumentSource(GetSortedOverlayDocuments()),
            CreateDeletionKeys(_deletedPaths),
            cancellationToken);
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

        return dominantPlan with
        {
            ScopeDocumentCount = basePlan.ScopeDocumentCount + overlayPlan.ScopeDocumentCount,
            ScopeByteCount = basePlan.ScopeByteCount + overlayPlan.ScopeByteCount,
            UsesIndex = basePlan.UsesIndex || overlayPlan.UsesIndex,
            UsesPathIndex = basePlan.UsesPathIndex || overlayPlan.UsesPathIndex,
            CanUseLiteralVerifier = basePlan.CanUseLiteralVerifier && overlayPlan.CanUseLiteralVerifier,
            CanUseNonBacktracking = basePlan.CanUseNonBacktracking && overlayPlan.CanUseNonBacktracking,
            NeedsUnicodeDecode = basePlan.NeedsUnicodeDecode || overlayPlan.NeedsUnicodeDecode,
            MayRequireWholeDocumentVerification = basePlan.MayRequireWholeDocumentVerification || overlayPlan.MayRequireWholeDocumentVerification,
            EstimatedPathFilterCost = basePlan.EstimatedPathFilterCost + overlayPlan.EstimatedPathFilterCost,
            EstimatedDirectScanCost = basePlan.EstimatedDirectScanCost + overlayPlan.EstimatedDirectScanCost,
            EstimatedIndexedCost = SumNullable(basePlan.EstimatedIndexedCost, overlayPlan.EstimatedIndexedCost),
            EstimatedCandidateDocuments = basePlan.EstimatedCandidateDocuments + overlayPlan.EstimatedCandidateDocuments,
            EstimatedCost = basePlan.EstimatedCost + overlayPlan.EstimatedCost,
            SelectedNgrams = MergeSelectedNgrams(basePlan.SelectedNgrams, overlayPlan.SelectedNgrams),
            MandatoryFragments = MergeStrings(basePlan.MandatoryFragments, overlayPlan.MandatoryFragments),
            AdvisorySeeds = MergeStrings(basePlan.AdvisorySeeds, overlayPlan.AdvisorySeeds)
        };
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
            return overlayStats with { StopReason = SearchStopReason.MaxHits };
        }

        if (remainingDocuments <= 0)
        {
            return overlayStats with { StopReason = SearchStopReason.MaxDocuments };
        }

        if (remainingBytes <= 0)
        {
            return overlayStats with { StopReason = SearchStopReason.MaxBytesScanned };
        }

        var remainingElapsedMilliseconds = query.MaxElapsedMilliseconds - watch.ElapsedMilliseconds;
        if (remainingElapsedMilliseconds <= 0)
        {
            return overlayStats with { StopReason = SearchStopReason.MaxElapsedTime };
        }

        var hiddenPaths = BuildHiddenPathList();
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
            new HiddenPathVisibility(hiddenPaths),
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
        var normalizedPath = IndexBuilder.CanonicalizePath(key.Path);
        if (_overlayDocuments.TryGetValue(normalizedPath, out var document))
        {
            content = document.Content;
            return true;
        }

        if (_deletedPaths.Contains(normalizedPath))
        {
            content = default;
            return false;
        }

        return _baseSnapshot.TryGetDocumentBytes(new DocumentKey(normalizedPath), out content);
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

    private byte[][] BuildHiddenPathList()
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

        return hidden;
    }

    private List<StoredDocument> GetSortedOverlayDocuments()
    {
        var documents = new List<StoredDocument>(_overlayDocuments.Count);
        foreach (var document in _overlayDocuments.Values)
        {
            documents.Add(document);
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

    private static string[] MergeStrings(string[] left, string[] right)
    {
        if (left.Length == 0)
        {
            return right;
        }

        if (right.Length == 0)
        {
            return left;
        }

        var merged = new List<string>(left.Length + right.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < left.Length; i++)
        {
            if (seen.Add(left[i]))
            {
                merged.Add(left[i]);
            }
        }

        for (var i = 0; i < right.Length; i++)
        {
            if (seen.Add(right[i]))
            {
                merged.Add(right[i]);
            }
        }

        return [.. merged];
    }

    private static SelectedNGram[] MergeSelectedNgrams(SelectedNGram[] left, SelectedNGram[] right)
    {
        if (left.Length == 0)
        {
            return right;
        }

        if (right.Length == 0)
        {
            return left;
        }

        var merged = new List<SelectedNGram>(left.Length + right.Length);
        var seen = new HashSet<(string Text, int Length)>();
        for (var i = 0; i < left.Length; i++)
        {
            var key = (left[i].Text, left[i].Length);
            if (seen.Add(key))
            {
                merged.Add(left[i]);
            }
        }

        for (var i = 0; i < right.Length; i++)
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
            document = new SubmittedDocument(next.Path, next.Content, new DateTimeOffset(next.MTimeUtcTicks, TimeSpan.Zero));
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
        private readonly byte[][] _hiddenPaths;

        public HiddenPathVisibility(byte[][] hiddenPaths)
        {
            _hiddenPaths = hiddenPaths;
        }

        public bool IsVisible(ReadOnlySpan<byte> normalizedPathUtf8)
        {
            for (var i = 0; i < _hiddenPaths.Length; i++)
            {
                if (normalizedPathUtf8.SequenceEqual(_hiddenPaths[i]))
                {
                    return false;
                }
            }

            return true;
        }
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
