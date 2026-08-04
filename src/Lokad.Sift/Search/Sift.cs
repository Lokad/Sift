using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Lokad.Sift.Indexer;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Search;
using Lokad.Sift.Storage;

namespace Lokad.Sift;

public sealed class Sift : ISift
{
    private delegate ValueTask<StreamedDocuments> UpdateDocumentStreamer(
        IReadOnlySet<string> forbiddenPaths,
        RejectedUpsertPolicy rejectedUpsertPolicy,
        Action<List<StoredDocument>> preparePartition,
        Action<IReadOnlyList<StoredDocument>> publishPartition,
        CancellationToken cancellationToken);

    private const long EstimatedReplacementPackOverheadBytes = 64L * 1024;
    private readonly ISiftStorage _storage;
    private readonly IndexBuilder _builder;
    private readonly SiftOptions _options;
    private readonly IDisposable? _retainedIndexingWorkspaceLease;
    private readonly MutationCoordinator _mutations = new();
    private readonly CancellationTokenSource _reclamationCancellation = new();
    private readonly ManualResetEventSlim _reclamationIdle = new(initialState: true);
    private readonly CancellationTokenSource _validationCancellation = new();
    private readonly ManualResetEventSlim _validationsIdle = new(initialState: true);
    private readonly SemaphoreSlim _validationGate = new(1, 1);
    private readonly ConcurrentDictionary<LegacyMaintenanceKey, long> _legacyDeadContentBytes = new();
    // Makes worker ownership and the idle signal atomic with respect to new requests and Dispose.
    private readonly object _reclamationSync = new();
    // Prevents Dispose from racing a mutation between its disposed check and activity registration.
    private readonly object _activitySync = new();
    private readonly object _validationStatsSync = new();
    private IndexValidationStats _validationStats = new(
        IndexValidationState.NotStarted,
        Generation: null,
        TotalSegments: 0,
        ValidatedSegments: 0,
        TotalBytes: 0,
        ValidatedBytes: 0,
        ElapsedMilliseconds: 0,
        Failure: null);
    private int _activeValidations;
    private int _reclamationRequests;
    private int _reclamationWorkerActive;
    private int _disposed;
    private long _validationStatsRevision;
    private Exception? _rebuildCause;
    internal ISiftStorage Storage => _storage;
    public bool RequiresRebuild => Volatile.Read(ref _rebuildCause) is not null;

    public Sift(string rootPath)
        : this(rootPath, new SiftOptions())
    {
    }

    public Sift(string rootPath, SiftOptions options)
        : this(new CorpusStorage(rootPath, options.MaximumPhysicalBytes), options)
    {
    }

    internal Sift(ISiftStorage storage, SiftOptions options)
    {
        _options = options;
        ValidateSiftOptions(_options);
        _retainedIndexingWorkspaceLease = options.RetainIndexingWorkspace
            ? SegmentIndexBuilder.AcquireWorkspaceLease()
            : null;
        _storage = storage;
        _builder = new IndexBuilder(_storage, _options);
        try
        {
            _storage.EnsureSnapshotMetadata();
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            _rebuildCause = exception;
            return;
        }
        catch
        {
            _retainedIndexingWorkspaceLease?.Dispose();
            throw;
        }

        ScheduleReclamationIfPending();
    }

    public static Sift CreateInMemory()
    {
        return CreateInMemory(new SiftOptions());
    }

    public static Sift CreateInMemory(SiftOptions options)
    {
        return new Sift(new InMemorySiftStorage(), options);
    }

    public IIndexSnapshot OpenSnapshot(SearchOptions options)
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        try
        {
            return new SearchSnapshot(
                _storage,
                _options,
                options,
                ScheduleReclamationIfPending,
                MarkRequiresRebuild);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    public IOverlaySnapshot OpenOverlaySnapshot(SearchOptions options)
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        try
        {
            return new OverlaySnapshot(
                new SearchSnapshot(
                    _storage,
                    _options,
                    options,
                    ScheduleReclamationIfPending,
                    MarkRequiresRebuild),
                _options,
                options);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    public Task<BuildStats> Build(
        IDocumentSource documents,
        BuildOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(options);
        return RunBuild(
            operationCancellation => BuildCore(
                (isRecovery, token) => _builder.BuildAsync(documents, options, isRecovery, token),
                operationCancellation),
            cancellationToken);
    }

    public Task<BuildStats> Build(
        IAsyncEnumerable<SubmittedDocument> documents,
        BuildOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(options);
        return RunBuild(
            operationCancellation => BuildCore(
                (isRecovery, token) => _builder.BuildAsync(documents, options, isRecovery, token),
                operationCancellation),
            cancellationToken);
    }

    private Task<BuildStats> RunBuild(
        Func<CancellationToken, ValueTask<BuildStats>> build,
        CancellationToken cancellationToken)
    {
        return RunMutation(
            SiftActivity.Build,
            async operationCancellation =>
            {
                if (!_options.MeasurePeakManagedMemory)
                {
                    return await build(operationCancellation).ConfigureAwait(false);
                }

                using var measurement = ManagedMemoryPeakMeasurement.Start();
                var stats = await build(operationCancellation).ConfigureAwait(false);
                var peakManagedBytes = await measurement.CompleteAsync().ConfigureAwait(false);
                return stats with { PeakManagedBytes = peakManagedBytes };
            },
            cancellationToken);
    }

    private async Task<TResult> RunMutation<TResult>(
        SiftActivity activity,
        Func<CancellationToken, ValueTask<TResult>> mutation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await _mutations.Run(activity, mutation, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<BuildStats> BuildCore(
        Func<bool, CancellationToken, ValueTask<BuildStats>> build,
        CancellationToken cancellationToken)
    {
        var isRecovery = RequiresRebuild;
        ResetValidationStats();
        try
        {
            var stats = await build(isRecovery, cancellationToken).ConfigureAwait(false);
            ResetValidationStats();
            Volatile.Write(ref _rebuildCause, null);
            return stats;
        }
        catch (Exception exception) when (isRecovery)
        {
            MarkRequiresRebuild(new InvalidDataException(
                "The full Sift rebuild did not complete.",
                exception));
            throw;
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    public ActivityStats GetActivityStats()
    {
        ThrowIfDisposed();
        return _mutations.GetActivityStats();
    }

    public IndexValidationStats GetValidationStats()
    {
        ThrowIfDisposed();
        lock (_validationStatsSync)
        {
            return _validationStats;
        }
    }

    public IndexIdentity GetIndexIdentity()
    {
        return GetIndexState().Identity;
    }

    public IndexState GetIndexState()
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        try
        {
            using var snapshot = _storage.OpenReadSnapshot();
            return new IndexState(
                CreateIndexIdentity(snapshot.Manifest),
                snapshot.Manifest.CommitMetadata.ToArray());
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    public Task<IndexValidationStats> Validate(CancellationToken cancellationToken)
    {
        ThrowIfRebuildRequired();
        var validation = BeginValidation();
        return Task.Run(() =>
        {
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _validationCancellation.Token);
            var acquiredGate = false;
            var validationRevision = 0L;
            try
            {
                _validationGate.Wait(operationCancellation.Token);
                acquiredGate = true;
                validationRevision = BeginValidationAttempt();
                return ValidateCore(operationCancellation.Token, validationRevision);
            }
            catch (OperationCanceledException)
            {
                if (acquiredGate)
                {
                    TrySetValidationState(
                        validationRevision,
                        IndexValidationState.Cancelled,
                        failure: null);
                }

                throw;
            }
            catch (Exception exception)
            {
                if (acquiredGate)
                {
                    TryFailValidationAttempt(
                        validationRevision,
                        exception);
                }

                throw;
            }
            finally
            {
                if (acquiredGate)
                {
                    _validationGate.Release();
                }

                validation.Dispose();
            }
        });
    }

    private IndexValidationStats ValidateCore(
        CancellationToken cancellationToken,
        long validationRevision)
    {
        long GetSegmentBytes(ulong segmentId)
        {
            try
            {
                return _storage.GetSegmentBytes(segmentId);
            }
            catch (Exception exception) when (IsIndexCorruption(exception))
            {
                PreserveRetainedSegmentFailure(
                    validationRevision,
                    segmentId,
                    exception,
                    cancellationToken);
                throw;
            }
        }

        void ValidateSegment(ulong segmentId)
        {
            try
            {
                _storage.ValidateSegment(segmentId, cancellationToken);
            }
            catch (Exception exception) when (IsIndexCorruption(exception))
            {
                PreserveRetainedSegmentFailure(
                    validationRevision,
                    segmentId,
                    exception,
                    cancellationToken);
                throw;
            }
        }

        var watch = Stopwatch.StartNew();
        using var snapshot = _storage.OpenReadSnapshot();
        var manifest = snapshot.Manifest;
        var segmentBytes = new long[manifest.Segments.Count];
        long totalBytes = 0;
        for (var i = 0; i < manifest.Segments.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            segmentBytes[i] = GetSegmentBytes(manifest.Segments[i].SegmentId);
            totalBytes = checked(totalBytes + segmentBytes[i]);
        }

        if (!TrySetValidationStats(validationRevision, new IndexValidationStats(
            IndexValidationState.Running,
            manifest.Generation,
            manifest.Segments.Count,
            ValidatedSegments: 0,
            totalBytes,
            ValidatedBytes: 0,
            ElapsedMilliseconds: 0,
            Failure: null)))
        {
            return GetValidationStats();
        }

        long validatedBytes = 0;
        for (var i = 0; i < manifest.Segments.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSegment(manifest.Segments[i].SegmentId);
            validatedBytes += segmentBytes[i];
            if (!TrySetValidationStats(validationRevision, new IndexValidationStats(
                IndexValidationState.Running,
                manifest.Generation,
                manifest.Segments.Count,
                i + 1,
                totalBytes,
                validatedBytes,
                watch.ElapsedMilliseconds,
                Failure: null)))
            {
                return GetValidationStats();
            }
        }

        watch.Stop();
        var completed = new IndexValidationStats(
            IndexValidationState.Ready,
            manifest.Generation,
            manifest.Segments.Count,
            manifest.Segments.Count,
            totalBytes,
            totalBytes,
            watch.ElapsedMilliseconds,
            Failure: null);
        return TrySetValidationStats(validationRevision, completed)
            ? completed
            : GetValidationStats();
    }

    public MaintenanceStats GetMaintenanceStats()
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        try
        {
            return GetMaintenanceStatsCore();
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    private MaintenanceStats GetMaintenanceStatsCore()
    {
        using var snapshot = _storage.OpenReadSnapshot();
        var manifest = snapshot.Manifest;
        var segmentCount = manifest.Segments.Count;
        var totalDocuments = 0;
        var deadDocuments = 0;
        long contentBytes = 0;
        long reclaimableContentBytes = 0;
        var segmentsWithDeletions = 0;
        var smallSegmentCount = 0;
        var currentLegacyKeys = new HashSet<LegacyMaintenanceKey>();

        foreach (var segment in manifest.Segments)
        {
            totalDocuments += segment.DocumentCount;
            deadDocuments += segment.DeadOrdinals.Length;
            contentBytes += segment.ContentBytes;
            if (segment.DeadOrdinals.Length > 0)
            {
                segmentsWithDeletions++;
                reclaimableContentBytes = checked(
                    reclaimableContentBytes +
                    GetDeadContentBytes(segment, currentLegacyKeys, CancellationToken.None));
            }

            if (segment.LiveDocumentCount > 0 &&
                segment.LiveDocumentCount < _options.TargetSegmentDocumentCount &&
                segment.ContentBytes < _options.TargetSegmentContentBytes)
            {
                smallSegmentCount++;
            }
        }

        foreach (var key in _legacyDeadContentBytes.Keys)
        {
            if (!currentLegacyKeys.Contains(key))
            {
                _legacyDeadContentBytes.TryRemove(key, out _);
            }
        }

        var liveDocuments = totalDocuments - deadDocuments;
        var deadDocumentFraction = totalDocuments == 0 ? 0d : (double)deadDocuments / totalDocuments;
        var deadContentFraction = contentBytes == 0 ? 0d : (double)reclaimableContentBytes / contentBytes;
        var estimatedLiveContentBytes = contentBytes - reclaimableContentBytes;

        return new MaintenanceStats(
            Generation: manifest.Generation,
            SegmentCount: segmentCount,
            TotalDocuments: totalDocuments,
            LiveDocuments: liveDocuments,
            DeadDocuments: deadDocuments,
            DeadDocumentFraction: deadDocumentFraction,
            DeadContentFraction: deadContentFraction,
            ContentBytes: contentBytes,
            EstimatedLiveContentBytes: estimatedLiveContentBytes,
            EstimatedReclaimableContentBytes: reclaimableContentBytes,
            DeadOrdinalEntries: deadDocuments,
            SegmentsWithDeletions: segmentsWithDeletions,
            SmallSegmentCount: smallSegmentCount);
    }

    public StorageStats GetStorageStats()
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        try
        {
            return _storage.GetStorageStats();
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    public bool ShouldCompact(CompactOptions options)
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        ValidateCompactOptions(options);
        try
        {
            using var writerLock = _storage.AcquireWriterLock();
            ThrowIfRebuildRequired();
            if (!_storage.HasManifest ||
                !_storage.CanReplaceSegmentsUnderWriterLock())
            {
                return false;
            }

            return HasActionableCompactionWorkUnderWriterLock(
                _storage.ReadManifest(),
                options,
                CancellationToken.None);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    public Task<UpdateStats> Update(
        IDocumentSource upserts,
        ReadOnlySpan<DocumentKey> deletions,
        CancellationToken cancellationToken)
    {
        return Update(
            upserts,
            deletions,
            new UpdateOptions(_options.RejectedUpsertPolicy),
            cancellationToken);
    }

    public Task<UpdateStats> Update(
        IDocumentSource upserts,
        ReadOnlySpan<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        return RunUpdate(
            (forbiddenPaths, rejectedUpsertPolicy, preparePartition, publishPartition, operationCancellation) =>
                new ValueTask<StreamedDocuments>(IndexBuilder.StreamDocuments(
                    upserts,
                    operationCancellation,
                    _options,
                    operationName: "update",
                    forbiddenPaths,
                    rejectedUpsertPolicy,
                    collectAcceptedPaths: true,
                    preparePartition,
                    publishPartition)),
            deletions,
            options,
            cancellationToken);
    }

    public Task<UpdateStats> Update(
        IAsyncEnumerable<SubmittedDocument> upserts,
        ReadOnlySpan<DocumentKey> deletions,
        CancellationToken cancellationToken)
    {
        return Update(
            upserts,
            deletions,
            new UpdateOptions(_options.RejectedUpsertPolicy),
            cancellationToken);
    }

    public Task<UpdateStats> Update(
        IAsyncEnumerable<SubmittedDocument> upserts,
        ReadOnlySpan<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        return RunUpdate(
            (forbiddenPaths, rejectedUpsertPolicy, preparePartition, publishPartition, operationCancellation) =>
                IndexBuilder.StreamDocumentsAsync(
                    upserts,
                    operationCancellation,
                    _options,
                    operationName: "update",
                    forbiddenPaths,
                    rejectedUpsertPolicy,
                    collectAcceptedPaths: true,
                    preparePartition,
                    publishPartition),
            deletions,
            options,
            cancellationToken);
    }

    private Task<UpdateStats> RunUpdate(
        UpdateDocumentStreamer streamDocuments,
        ReadOnlySpan<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        ValidateUpdateOptions(options);
        var deletionArray = deletions.ToArray();
        return RunMutation(
            SiftActivity.Update,
            async operationCancellation =>
            {
                try
                {
                    return await UpdateCoreAsync(
                        streamDocuments,
                        deletionArray,
                        options,
                        operationCancellation).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsIndexCorruption(exception))
                {
                    MarkRequiresRebuild(exception);
                    throw;
                }
            },
            cancellationToken);
    }

    private async ValueTask<UpdateStats> UpdateCoreAsync(
        UpdateDocumentStreamer streamDocuments,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        static uint[] MergeDeadOrdinals(uint[] existing, List<LiveDocumentKey> added)
        {
            var merged = new uint[existing.Length + added.Count];
            var existingIndex = 0;
            var addedIndex = 0;
            var mergedIndex = 0;
            while (existingIndex < existing.Length && addedIndex < added.Count)
            {
                if (existing[existingIndex] < added[addedIndex].Ordinal)
                {
                    merged[mergedIndex++] = existing[existingIndex++];
                }
                else
                {
                    merged[mergedIndex++] = added[addedIndex++].Ordinal;
                }
            }

            existing.AsSpan(existingIndex).CopyTo(merged.AsSpan(mergedIndex));
            mergedIndex += existing.Length - existingIndex;
            while (addedIndex < added.Count)
            {
                merged[mergedIndex++] = added[addedIndex++].Ordinal;
            }

            return merged;
        }

        var watch = Stopwatch.StartNew();
        using var _ = _storage.AcquireWriterLock(cancellationToken);
        ThrowIfRebuildRequired();
        var hadManifest = _storage.HasManifest;
        var currentManifest = hadManifest ? _storage.ReadManifest() : null;
        var actualIdentity = currentManifest is null ? default : CreateIndexIdentity(currentManifest);
        var committedIdentity = actualIdentity;
        if (options.ExpectedIdentity is { } expectedIdentity &&
            (expectedIdentity.CorpusId != actualIdentity.CorpusId ||
             expectedIdentity.ContentRevision != actualIdentity.ContentRevision))
        {
            throw new StaleIndexStateException(expectedIdentity, actualIdentity);
        }

        var existingManifest = currentManifest ?? new CorpusManifest(
            Guid.NewGuid(),
            ContentRevision: 0,
            Generation: 0,
            CommitMetadata: [],
            Segments: []);

        var deletionSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deletion in deletions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(deletion);
            var normalized = IndexBuilder.CanonicalizePath(deletion.Path);
            if (!deletionSet.Add(normalized))
            {
                throw new InvalidOperationException($"Duplicate deletion path in update batch: '{normalized}'.");
            }
        }

        var publishedSegments = new List<SegmentManifestEntry>();
        var attemptedSegmentIds = new List<ulong>();
        var existingUpsertKeys = new Dictionary<string, LiveDocumentKey>(StringComparer.Ordinal);
        var deletedDocuments = 0;
        StreamedDocuments upsertLoad;
        try
        {
            var segmentSeed = (existingManifest.Generation + 1UL) ^ ((ulong)DateTime.UtcNow.Ticks << 1);
            upsertLoad = await streamDocuments(
                deletionSet,
                options.RejectedUpsertPolicy,
                partition => RemoveUnchangedDocuments(
                    existingManifest,
                    partition,
                    existingUpsertKeys,
                    cancellationToken),
                partition =>
                {
                    var segmentId = segmentSeed + (ulong)publishedSegments.Count;
                    attemptedSegmentIds.Add(segmentId);
                    publishedSegments.Add(_storage.PublishSegment(
                        partition,
                        segmentId,
                        _options.MinimumFreeBytesAfterMutation,
                        cancellationToken));
                },
                cancellationToken).ConfigureAwait(false);
            var removalPaths = new HashSet<string>(deletionSet, StringComparer.Ordinal);
            if (options.RejectedUpsertPolicy == RejectedUpsertPolicy.DeleteExisting)
            {
                removalPaths.UnionWith(upsertLoad.RejectedDocuments.Select(static document => document.Path));
            }

            var affectedPaths = new HashSet<string>(upsertLoad.AcceptedPaths, StringComparer.Ordinal);
            affectedPaths.UnionWith(removalPaths);
            var liveDocumentKeys = new Dictionary<string, LiveDocumentKey>(affectedPaths.Count, StringComparer.Ordinal);
            foreach (var path in upsertLoad.AcceptedPaths)
            {
                if (existingUpsertKeys.TryGetValue(path, out var key))
                {
                    liveDocumentKeys.Add(path, key);
                }
            }

            foreach (var pair in FindLiveDocumentKeys(
                existingManifest,
                removalPaths,
                cancellationToken))
            {
                liveDocumentKeys.Add(pair.Key, pair.Value);
            }
            foreach (var path in removalPaths)
            {
                if (liveDocumentKeys.ContainsKey(path))
                {
                    deletedDocuments++;
                }
            }

            var requiresManifestMigration = existingManifest.Segments.Any(
                static segment => segment.DeadContentBytes < 0);
            var pendingStats = new UpdateStats(
                SubmittedUpserts: upsertLoad.Submitted,
                UpsertedDocuments: upsertLoad.Accepted,
                SkippedUpserts: upsertLoad.Skipped,
                RejectedUpserts: upsertLoad.Rejected,
                DeletedDocuments: deletedDocuments,
                PublishedSegments: publishedSegments.Count,
                SubmittedBytes: upsertLoad.SubmittedBytes,
                ContentBytes: upsertLoad.AcceptedBytes,
                SkippedBytes: upsertLoad.SkippedBytes,
                RejectedBytes: upsertLoad.RejectedBytes,
                RejectedDocuments: upsertLoad.RejectedDocuments,
                ElapsedMilliseconds: watch.ElapsedMilliseconds);
            var commitMetadata = options.CommitMetadataProvider is null
                ? existingManifest.CommitMetadata
                : options.CommitMetadataProvider(pendingStats).ToArray();
            if (commitMetadata.Length > _options.MaxCommitMetadataBytes)
            {
                throw new InvalidOperationException(
                    $"Sift commit metadata exceeds the configured {_options.MaxCommitMetadataBytes} byte limit.");
            }

            var contentChanged = !hadManifest ||
                publishedSegments.Count > 0 ||
                liveDocumentKeys.Count > 0;
            var commitMetadataChanged = !commitMetadata.AsSpan().SequenceEqual(existingManifest.CommitMetadata);
            if (contentChanged || requiresManifestMigration || commitMetadataChanged)
            {
                var newSegments = new List<SegmentManifestEntry>(existingManifest.Segments.Count + publishedSegments.Count);
                var newlyDeadBySegment = new Dictionary<ulong, List<LiveDocumentKey>>();

                // Retire prior versions replaced or removed by this update.
                foreach (var path in affectedPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (liveDocumentKeys.TryGetValue(path, out var current))
                    {
                        if (!newlyDeadBySegment.TryGetValue(current.SegmentId, out var newlyDead))
                        {
                            newlyDead = [];
                            newlyDeadBySegment.Add(current.SegmentId, newlyDead);
                        }

                        newlyDead.Add(current);
                    }
                }

                foreach (var segment in existingManifest.Segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Preserve both the entry and its dead-ordinal array when this
                    // batch did not replace or delete anything in the segment.
                    if (!newlyDeadBySegment.TryGetValue(segment.SegmentId, out var newlyDead))
                    {
                        newSegments.Add(segment);
                        continue;
                    }

                    newlyDead.Sort(static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
                    var deadContentBytes = segment.DeadContentBytes;
                    if (deadContentBytes >= 0)
                    {
                        foreach (var document in newlyDead)
                        {
                            deadContentBytes = checked(deadContentBytes + document.ContentLength);
                        }
                    }

                    newSegments.Add(segment with
                    {
                        DeadOrdinals = MergeDeadOrdinals(segment.DeadOrdinals, newlyDead),
                        DeadContentBytes = deadContentBytes
                    });
                }

                newSegments.AddRange(publishedSegments);

                var manifest = new CorpusManifest(
                    existingManifest.CorpusId,
                    existingManifest.ContentRevision + (contentChanged ? 1UL : 0UL),
                    existingManifest.Generation + 1UL,
                    commitMetadata.ToArray(),
                    newSegments);
                ResetValidationStats();
                _storage.PublishManifest(manifest);
                ResetValidationStats();
                committedIdentity = CreateIndexIdentity(manifest);
            }
        }
        catch
        {
            _storage.CleanupFailedMutationUnderWriterLock(attemptedSegmentIds);
            throw;
        }

        watch.Stop();
        return new UpdateStats(
            SubmittedUpserts: upsertLoad.Submitted,
            UpsertedDocuments: upsertLoad.Accepted,
            SkippedUpserts: upsertLoad.Skipped,
            RejectedUpserts: upsertLoad.Rejected,
            DeletedDocuments: deletedDocuments,
            PublishedSegments: publishedSegments.Count,
            SubmittedBytes: upsertLoad.SubmittedBytes,
            ContentBytes: upsertLoad.AcceptedBytes,
            SkippedBytes: upsertLoad.SkippedBytes,
            RejectedBytes: upsertLoad.RejectedBytes,
            RejectedDocuments: upsertLoad.RejectedDocuments,
            ElapsedMilliseconds: watch.ElapsedMilliseconds)
        {
            CommittedIdentity = committedIdentity
        };
    }

    private static void ValidateUpdateOptions(UpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.RejectedUpsertPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RejectedUpsertPolicy is invalid.");
        }

    }

    private static IndexIdentity CreateIndexIdentity(CorpusManifest manifest)
    {
        return new IndexIdentity(
            manifest.CorpusId,
            manifest.ContentRevision,
            manifest.Generation);
    }

    public Task<CompactStats> Compact(CompactOptions options, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        ValidateCompactOptions(options);
        return RunMutation(
            SiftActivity.Compact,
            async operationCancellation =>
            {
                try
                {
                    if (!_options.MeasurePeakManagedMemory)
                    {
                        return CompactCore(options, operationCancellation);
                    }

                    using var measurement = ManagedMemoryPeakMeasurement.Start();
                    var stats = CompactCore(options, operationCancellation);
                    var peakManagedBytes = await measurement.CompleteAsync().ConfigureAwait(false);
                    return stats with { PeakManagedBytes = peakManagedBytes };
                }
                catch (Exception exception) when (IsIndexCorruption(exception))
                {
                    MarkRequiresRebuild(exception);
                    throw;
                }
            },
            cancellationToken);
    }

    private CompactStats CompactCore(CompactOptions options, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var _ = _storage.AcquireWriterLock(cancellationToken);
        ThrowIfRebuildRequired();
        var manifest = _storage.HasManifest
            ? _storage.ReadManifest()
            : new CorpusManifest(Guid.NewGuid(), 0, []);
        var selectedSegmentIds = SelectSegmentsForCompaction(
            manifest,
            options,
            cancellationToken);
        if (selectedSegmentIds.Count == 0)
        {
            watch.Stop();
            return new CompactStats(
                InputSegments: 0,
                OutputSegments: 0,
                MergedDocuments: 0,
                ContentBytes: 0,
                ElapsedMilliseconds: watch.ElapsedMilliseconds,
                PeakManagedBytes: 0,
                HasMoreWork: false);
        }

        _storage.EnsureReplacementAllowedUnderWriterLock();

        var segmentSeed = (manifest.Generation + 1UL) ^ ((ulong)DateTime.UtcNow.Ticks << 1);
        var attemptedSegmentIds = new List<ulong>();
        var newSegments = new List<SegmentManifestEntry>();
        CompactionPublication publication;
        try
        {
            publication = PublishCompactedSegments(
                manifest,
                selectedSegmentIds,
                segmentSeed,
                attemptedSegmentIds,
                newSegments,
                options,
                cancellationToken);

            var retainedSegments = manifest.Segments
                .Where(entry => !selectedSegmentIds.Contains(entry.SegmentId))
                .ToArray();
            var mergedSegments = new SegmentManifestEntry[retainedSegments.Length + newSegments.Count];
            Array.Copy(retainedSegments, mergedSegments, retainedSegments.Length);
            for (var i = 0; i < newSegments.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                mergedSegments[retainedSegments.Length + i] = newSegments[i];
            }

            var newManifest = new CorpusManifest(
                manifest.CorpusId,
                manifest.ContentRevision,
                manifest.Generation + 1UL,
                [.. manifest.CommitMetadata],
                mergedSegments);
            ResetValidationStats();
            _storage.PublishManifest(newManifest);
            ResetValidationStats();
            manifest = newManifest;
        }
        catch
        {
            _storage.CleanupFailedMutationUnderWriterLock(attemptedSegmentIds);
            throw;
        }

        watch.Stop();
        return new CompactStats(
            InputSegments: selectedSegmentIds.Count,
            OutputSegments: newSegments.Count,
            MergedDocuments: publication.MergedDocuments,
            ContentBytes: publication.ContentBytes,
            ElapsedMilliseconds: watch.ElapsedMilliseconds,
            PeakManagedBytes: 0,
            HasMoreWork: _storage.CanReplaceSegmentsUnderWriterLock() &&
                HasActionableCompactionWorkUnderWriterLock(
                    manifest,
                    options,
                    CancellationToken.None));
    }

    private bool HasActionableCompactionWorkUnderWriterLock(
        CorpusManifest manifest,
        CompactOptions options,
        CancellationToken cancellationToken)
    {
        var selectedSegmentIds = SelectSegmentsForCompaction(
            manifest,
            options,
            cancellationToken);
        return selectedSegmentIds.Count > 0 &&
               HasEstimatedCompactionCapacity(
                   manifest,
                   selectedSegmentIds,
                   options,
                   cancellationToken);
    }

    public void Dispose()
    {
        lock (_activitySync)
        {
            if (_disposed != 0)
            {
                return;
            }

            Volatile.Write(ref _disposed, 1);
            _validationCancellation.Cancel();
        }

        _mutations.Stop();
        _mutations.WaitForIdle();
        _validationsIdle.Wait();
        lock (_reclamationSync)
        {
            _reclamationCancellation.Cancel();
        }

        _reclamationIdle.Wait();
        _retainedIndexingWorkspaceLease?.Dispose();
        _mutations.Dispose();
        _validationCancellation.Dispose();
        _validationsIdle.Dispose();
        _validationGate.Dispose();
        _reclamationCancellation.Dispose();
        _reclamationIdle.Dispose();
    }

    private void ScheduleReclamationIfPending()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            if (!_storage.IsReclamationPending)
            {
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        lock (_reclamationSync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _reclamationRequests = 1;
            if (_reclamationWorkerActive != 0)
            {
                return;
            }

            _reclamationWorkerActive = 1;
            _reclamationIdle.Reset();
            ThreadPool.UnsafeQueueUserWorkItem(
                static owner => owner.RunScheduledReclamation(),
                this,
                preferLocal: false);
        }
    }

    private void RunScheduledReclamation()
    {
        try
        {
            while (true)
            {
                lock (_reclamationSync)
                {
                    _reclamationRequests = 0;
                }

                try
                {
                    _storage.ReclaimUnreferencedSegments(_reclamationCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                }

                lock (_reclamationSync)
                {
                    if (_disposed == 0 && _reclamationRequests != 0)
                    {
                        continue;
                    }

                    _reclamationWorkerActive = 0;
                    _reclamationIdle.Set();
                    return;
                }
            }
        }
        finally
        {
            lock (_reclamationSync)
            {
                if (_reclamationWorkerActive != 0)
                {
                    _reclamationWorkerActive = 0;
                    _reclamationIdle.Set();
                }
            }
        }
    }

    private static void ValidateCompactOptions(CompactOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxMergedSegments <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMergedSegments must be positive.");
        }

        if (!double.IsFinite(options.DeadFractionThreshold) ||
            options.DeadFractionThreshold < 0d ||
            options.DeadFractionThreshold > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "DeadFractionThreshold must be between zero and one.");
        }

        if (!double.IsFinite(options.DeadContentFractionThreshold) ||
            options.DeadContentFractionThreshold < 0d ||
            options.DeadContentFractionThreshold > 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "DeadContentFractionThreshold must be between zero and one.");
        }

        if (options.SmallSegmentMergeThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SmallSegmentMergeThreshold must be positive.");
        }

        if (options.MetadataBloatDeadOrdinalsThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MetadataBloatDeadOrdinalsThreshold must be positive.");
        }

        if (options.MaxInputSegmentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxInputSegmentBytes must be positive.");
        }

        if (options.MinimumFreeBytesAfterCompaction < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MinimumFreeBytesAfterCompaction cannot be negative.");
        }
    }

    private static void ValidateSiftOptions(SiftOptions options)
    {
        if (options.MaxDocumentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDocumentBytes must be positive.");
        }

        if (options.TargetSegmentDocumentCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "TargetSegmentDocumentCount must be positive.");
        }

        if (options.TargetSegmentContentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "TargetSegmentContentBytes must be positive.");
        }

        if (options.MaximumPhysicalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumPhysicalBytes must be positive.");
        }

        if (options.MinimumFreeBytesAfterMutation < 0 ||
            options.MinimumFreeBytesAfterMutation >= options.MaximumPhysicalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MinimumFreeBytesAfterMutation must be non-negative and smaller than MaximumPhysicalBytes.");
        }

        if (options.DirectScanScopeDocumentsThreshold < 0 ||
            options.DirectScanScopeBytesThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Direct-scan thresholds cannot be negative.");
        }

        if (double.IsNaN(options.DirectScanCandidateDensityThreshold) ||
            options.DirectScanCandidateDensityThreshold < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "DirectScanCandidateDensityThreshold cannot be negative or NaN.");
        }

        if (options.MaxSelectedNgrams <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSelectedNgrams must be positive.");
        }

        if (options.MaxMaterializedMatchBytes <= 0 ||
            options.MaxMaterializedContextBytes < 0 ||
            options.MaxMaterializedContextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Materialization limits are invalid.");
        }

        if (options.MaxPatternCharacters <= 0 ||
            options.MaxPatternNesting <= 0 ||
            options.MaxPathFilterPatterns <= 0 ||
            options.MaxPathFilterCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Pattern limits must be positive.");
        }

        if (!Enum.IsDefined(options.RejectedUpsertPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RejectedUpsertPolicy is invalid.");
        }

        if (options.MaxCommitMetadataBytes < 0 ||
            options.MaxCommitMetadataBytes > SegmentFormat.MaximumCommitMetadataBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"MaxCommitMetadataBytes must be between zero and {SegmentFormat.MaximumCommitMetadataBytes}.");
        }

        if (options.OverlayCompactionSegmentThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "OverlayCompactionSegmentThreshold must be positive.");
        }

        if (options.MaxSegmentParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxSegmentParallelism must be positive.");
        }
    }

    private IDisposable BeginValidation()
    {
        lock (_activitySync)
        {
            ThrowIfDisposed();
            if (++_activeValidations == 1)
            {
                _validationsIdle.Reset();
            }
        }

        return new ValidationScope(this);
    }

    private void ResetValidationStats()
    {
        lock (_validationStatsSync)
        {
            _validationStatsRevision++;
            _validationStats = new IndexValidationStats(
                IndexValidationState.NotStarted,
                Generation: null,
                TotalSegments: 0,
                ValidatedSegments: 0,
                TotalBytes: 0,
                ValidatedBytes: 0,
                ElapsedMilliseconds: 0,
                Failure: null);
        }
    }

    private long BeginValidationAttempt()
    {
        lock (_validationStatsSync)
        {
            var revision = ++_validationStatsRevision;
            _validationStats = _validationStats with
            {
                State = IndexValidationState.Running,
                Failure = null
            };
            return revision;
        }
    }

    private bool TrySetValidationState(
        long validationRevision,
        IndexValidationState state,
        string? failure)
    {
        lock (_validationStatsSync)
        {
            if (_validationStatsRevision != validationRevision)
            {
                return false;
            }

            _validationStats = _validationStats with
            {
                State = state,
                Failure = failure
            };
            return true;
        }
    }

    private bool TrySetValidationStats(
        long validationRevision,
        IndexValidationStats stats)
    {
        lock (_validationStatsSync)
        {
            if (_validationStatsRevision != validationRevision)
            {
                return false;
            }

            _validationStats = stats;
            return true;
        }
    }

    private bool TryFailValidationAttempt(
        long validationRevision,
        Exception exception)
    {
        lock (_validationStatsSync)
        {
            if (_validationStatsRevision != validationRevision)
            {
                return false;
            }

            MarkRequiresRebuild(exception);
            _validationStats = _validationStats with
            {
                State = IndexValidationState.Failed,
                Failure = exception.Message
            };
            return true;
        }
    }

    private void PreserveRetainedSegmentFailure(
        long validationRevision,
        ulong failedSegmentId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        lock (_validationStatsSync)
        {
            if (_validationStatsRevision == validationRevision)
            {
                // The outer validation failure path records failures for the generation
                // that is still current.
                return;
            }
        }

        // A concurrent mutation can invalidate validation statistics while retaining
        // the immutable pack that failed. Resolve that edge under the writer lock so a
        // completed full build is not poisoned, while additive updates fail closed.
        IDisposable writerLock;
        try
        {
            writerLock = _storage.AcquireWriterLock(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation prevents exact reconciliation, so fail closed rather
            // than leave a known-corrupt pack searchable.
            MarkRequiresRebuild(exception);
            lock (_validationStatsSync)
            {
                _validationStats = _validationStats with
                {
                    State = IndexValidationState.Failed,
                    Failure = exception.Message
                };
            }

            throw;
        }

        using var _ = writerLock;
        Exception? currentFailure = null;
        try
        {
            if (!_storage.HasManifest ||
                _storage.ReadManifest().Segments.All(segment => segment.SegmentId != failedSegmentId))
            {
                return;
            }

            currentFailure = exception;
        }
        catch (Exception manifestException) when (IsIndexCorruption(manifestException))
        {
            currentFailure = manifestException;
        }

        MarkRequiresRebuild(currentFailure);
        lock (_validationStatsSync)
        {
            _validationStats = _validationStats with
            {
                State = IndexValidationState.Failed,
                Failure = currentFailure.Message
            };
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private void ThrowIfRebuildRequired()
    {
        var cause = Volatile.Read(ref _rebuildCause);
        if (cause is not null)
        {
            throw new InvalidDataException(
                "The Sift index is corrupt or incomplete. Run a full Build to replace it.",
                cause);
        }
    }

    private void MarkRequiresRebuild(Exception exception)
    {
        if (IsIndexCorruption(exception))
        {
            Interlocked.CompareExchange(ref _rebuildCause, exception, null);
        }
    }

    private static bool IsIndexCorruption(Exception exception)
    {
        return exception is InvalidDataException or FileNotFoundException or DirectoryNotFoundException or EndOfStreamException;
    }

    private sealed class ManagedMemoryPeakMeasurement : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _sampler;
        private long _peakManagedBytes;

        private ManagedMemoryPeakMeasurement()
        {
            _peakManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
            _sampler = Task.Run(async () =>
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
                    if (managedBytes > Volatile.Read(ref _peakManagedBytes))
                    {
                        Interlocked.Exchange(ref _peakManagedBytes, managedBytes);
                    }

                    try
                    {
                        await Task.Delay(5, _cancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        public static ManagedMemoryPeakMeasurement Start()
        {
            return new ManagedMemoryPeakMeasurement();
        }

        public async Task<long> CompleteAsync()
        {
            var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
            if (managedBytes > Volatile.Read(ref _peakManagedBytes))
            {
                Interlocked.Exchange(ref _peakManagedBytes, managedBytes);
            }

            _cancellation.Cancel();
            try
            {
                await _sampler.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            managedBytes = GC.GetTotalMemory(forceFullCollection: false);
            if (managedBytes > Volatile.Read(ref _peakManagedBytes))
            {
                Interlocked.Exchange(ref _peakManagedBytes, managedBytes);
            }

            return Volatile.Read(ref _peakManagedBytes);
        }

        public void Dispose()
        {
            _cancellation.Dispose();
        }
    }

    private Dictionary<string, LiveDocumentKey> FindLiveDocumentKeys(
        CorpusManifest manifest,
        IReadOnlySet<string> affectedPaths,
        CancellationToken cancellationToken)
    {
        return FindLiveDocumentKeys(
            manifest,
            affectedPaths,
            incomingDocuments: null,
            unchangedPaths: null,
            cancellationToken);
    }

    private Dictionary<string, LiveDocumentKey> FindLiveDocumentKeys(
        CorpusManifest manifest,
        IReadOnlySet<string> affectedPaths,
        IReadOnlyDictionary<string, StoredDocument>? incomingDocuments,
        ISet<string>? unchangedPaths,
        CancellationToken cancellationToken)
    {
        var live = new Dictionary<string, LiveDocumentKey>(affectedPaths.Count, StringComparer.Ordinal);
        if (affectedPaths.Count == 0)
        {
            return live;
        }

        var unresolvedPaths = affectedPaths.ToHashSet(StringComparer.Ordinal);
        string[]? sortedPaths = null;
        for (var entryIndex = manifest.Segments.Count - 1;
             entryIndex >= 0 && unresolvedPaths.Count > 0;
             entryIndex--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = manifest.Segments[entryIndex];
            using var segment = _storage.OpenSegment(entry.SegmentId, cancellationToken);
            var binarySearchComparisons = (long)unresolvedPaths.Count *
                (BitOperations.Log2((uint)Math.Max(segment.DocumentCount, 1)) + 1);
            if (binarySearchComparisons <= segment.DocumentCount)
            {
                // A small batch can target paths near the end of a large segment,
                // for which a merge scan would traverse nearly every document.
                foreach (var path in affectedPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!unresolvedPaths.Contains(path) ||
                        !segment.TryFindDocumentOrdinal(path, out var foundOrdinal))
                    {
                        continue;
                    }

                    unresolvedPaths.Remove(path);
                    if (Array.BinarySearch(entry.DeadOrdinals, foundOrdinal) < 0)
                    {
                        var document = segment.GetDocument(foundOrdinal);
                        var key = new LiveDocumentKey(
                            entry.SegmentId,
                            foundOrdinal,
                            document.ContentLength,
                            document.MTimeUtcTicks,
                            document.ContentHash64);
                        live.Add(path, key);
                        RecordUnchangedDocument(
                            segment,
                            document,
                            path,
                            key,
                            incomingDocuments,
                            unchangedPaths);
                    }
                }

                continue;
            }

            if (sortedPaths is null)
            {
                sortedPaths = affectedPaths.ToArray();
                Array.Sort(sortedPaths, StringComparer.Ordinal);
            }

            // Both inputs are ordinal-sorted, so one merge pass replaces a
            // binary search for every path in a large update batch.
            var pathIndex = 0;
            var ordinal = 0u;
            var deadIndex = 0;
            var comparisons = 0;
            while (pathIndex < sortedPaths.Length && ordinal < segment.DocumentCount)
            {
                while (pathIndex < sortedPaths.Length && !unresolvedPaths.Contains(sortedPaths[pathIndex]))
                {
                    pathIndex++;
                }

                if (pathIndex >= sortedPaths.Length)
                {
                    break;
                }

                if ((comparisons++ & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var document = segment.GetDocument(ordinal);
                var comparison = segment.ComparePathOrdinal(document, sortedPaths[pathIndex]);
                if (comparison < 0)
                {
                    ordinal++;
                    continue;
                }

                if (comparison > 0)
                {
                    pathIndex++;
                    continue;
                }

                while (deadIndex < entry.DeadOrdinals.Length && entry.DeadOrdinals[deadIndex] < ordinal)
                {
                    deadIndex++;
                }

                if (deadIndex >= entry.DeadOrdinals.Length || entry.DeadOrdinals[deadIndex] != ordinal)
                {
                    var path = sortedPaths[pathIndex];
                    var key = new LiveDocumentKey(
                        entry.SegmentId,
                        ordinal,
                        document.ContentLength,
                        document.MTimeUtcTicks,
                        document.ContentHash64);
                    live.Add(path, key);
                    RecordUnchangedDocument(
                        segment,
                        document,
                        path,
                        key,
                        incomingDocuments,
                        unchangedPaths);
                }

                unresolvedPaths.Remove(sortedPaths[pathIndex]);
                ordinal++;
                pathIndex++;
            }
        }

        return live;
    }

    private static void RecordUnchangedDocument(
        MappedSegment segment,
        SegmentDocument indexedDocument,
        string path,
        in LiveDocumentKey indexedKey,
        IReadOnlyDictionary<string, StoredDocument>? incomingDocuments,
        ISet<string>? unchangedPaths)
    {
        if (incomingDocuments is null ||
            unchangedPaths is null ||
            !incomingDocuments.TryGetValue(path, out var incoming) ||
            indexedKey.ContentLength != incoming.Content.Length ||
            indexedKey.ContentHash64 != incoming.ContentHash64 ||
            indexedKey.MTimeUtcTicks != incoming.MTimeUtcTicks)
        {
            return;
        }

        // The persisted hash is only a fast rejection filter; byte equality
        // is required before suppressing a replacement.
        if (segment.GetContentBytes(indexedDocument).SequenceEqual(incoming.Content.Span))
        {
            unchangedPaths.Add(path);
        }
    }

    private sealed class ValidationScope(Sift owner) : IDisposable
    {
        private Sift? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            lock (owner._activitySync)
            {
                if (--owner._activeValidations == 0)
                {
                    owner._validationsIdle.Set();
                }
            }
        }
    }

    private void RemoveUnchangedDocuments(
        CorpusManifest manifest,
        List<StoredDocument> documents,
        IDictionary<string, LiveDocumentKey> existingKeys,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0 || manifest.Segments.Count == 0)
        {
            return;
        }

        var byPath = documents.ToDictionary(static document => document.Path, StringComparer.Ordinal);
        var paths = byPath.Keys.ToHashSet(StringComparer.Ordinal);
        var unchangedPaths = new HashSet<string>(StringComparer.Ordinal);
        var current = FindLiveDocumentKeys(
            manifest,
            paths,
            byPath,
            unchangedPaths,
            cancellationToken);
        foreach (var pair in current)
        {
            existingKeys.Add(pair.Key, pair.Value);
        }

        documents.RemoveAll(document => unchangedPaths.Contains(document.Path));
    }

    private CompactionPublication PublishCompactedSegments(
        CorpusManifest manifest,
        HashSet<ulong> selectedSegmentIds,
        ulong segmentSeed,
        List<ulong> attemptedSegmentIds,
        List<SegmentManifestEntry> published,
        CompactOptions options,
        CancellationToken cancellationToken)
    {
        var cursors = new List<CompactionCursor>();
        try
        {
            foreach (var entry in manifest.Segments)
            {
                if (!selectedSegmentIds.Contains(entry.SegmentId))
                {
                    continue;
                }

                var cursor = new CompactionCursor(
                    _storage.OpenSegment(entry.SegmentId, cancellationToken),
                    entry.DeadOrdinals);
                if (cursor.MoveNext())
                {
                    cursors.Add(cursor);
                }
                else
                {
                    cursor.Dispose();
                }
            }

            var queue = new PriorityQueue<CompactionCursor, CompactionPriority>(CompactionPriorityComparer.Instance);
            for (var i = 0; i < cursors.Count; i++)
            {
                queue.Enqueue(cursors[i], new CompactionPriority(cursors[i].Current));
            }

            var currentPartition = new List<CompactionDocument>();
            long currentBytes = 0;
            var mergedDocuments = 0;
            long contentBytes = 0;

            while (queue.TryDequeue(out var cursor, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = cursor.Current;
                if (currentPartition.Count > 0 &&
                    (currentPartition.Count >= _options.TargetSegmentDocumentCount || currentBytes + next.ContentLength > _options.TargetSegmentContentBytes))
                {
                    var segmentId = segmentSeed + (ulong)published.Count;
                    attemptedSegmentIds.Add(segmentId);
                    published.Add(_storage.PublishSegment(
                        currentPartition,
                        segmentId,
                        options.MinimumFreeBytesAfterCompaction,
                        cancellationToken));
                    currentPartition = new List<CompactionDocument>();
                    currentBytes = 0;
                }

                currentPartition.Add(next);
                currentBytes += next.ContentLength;
                mergedDocuments++;
                contentBytes += next.ContentLength;

                if (cursor.MoveNext())
                {
                    queue.Enqueue(cursor, new CompactionPriority(cursor.Current));
                }
            }

            if (currentPartition.Count > 0)
            {
                var segmentId = segmentSeed + (ulong)published.Count;
                attemptedSegmentIds.Add(segmentId);
                published.Add(_storage.PublishSegment(
                    currentPartition,
                    segmentId,
                    options.MinimumFreeBytesAfterCompaction,
                    cancellationToken));
            }

            return new CompactionPublication(published, mergedDocuments, contentBytes);
        }
        finally
        {
            for (var i = 0; i < cursors.Count; i++)
            {
                cursors[i].Dispose();
            }
        }
    }

    private HashSet<ulong> SelectSegmentsForCompaction(
        CorpusManifest manifest,
        CompactOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = manifest.Segments
            .Select(entry => new
            {
                Entry = entry,
                DeadFraction = entry.DocumentCount == 0 ? 0d : (double)entry.DeadOrdinals.Length / entry.DocumentCount,
                DeadContentFraction = entry.ContentBytes == 0
                    ? 0d
                    : (double)GetDeadContentBytes(
                        entry,
                        currentLegacyKeys: null,
                        cancellationToken: cancellationToken) / entry.ContentBytes,
                HasMetadataBloat = entry.DeadOrdinals.Length >= options.MetadataBloatDeadOrdinalsThreshold
            })
            .OrderByDescending(item => item.DeadContentFraction)
            .ThenByDescending(item => item.DeadFraction)
            .ThenByDescending(item => item.HasMetadataBloat)
            .ThenBy(item => item.Entry.LiveDocumentCount)
            .ThenBy(item => item.Entry.SegmentId)
            .ToArray();

        var selected = new HashSet<ulong>();
        long selectedSegmentBytes = 0;
        foreach (var item in ordered.Where(item =>
                     item.DeadFraction >= options.DeadFractionThreshold ||
                     item.DeadContentFraction >= options.DeadContentFractionThreshold ||
                     item.HasMetadataBloat))
        {
            if (selected.Count >= options.MaxMergedSegments)
            {
                break;
            }

            var segmentBytes = _storage.GetSegmentBytes(item.Entry.SegmentId);
            if (selected.Count > 0 &&
                segmentBytes > options.MaxInputSegmentBytes - selectedSegmentBytes)
            {
                continue;
            }

            selected.Add(item.Entry.SegmentId);
            selectedSegmentBytes += segmentBytes;
        }

        if (selected.Count > 0 || manifest.Segments.Count <= options.SmallSegmentMergeThreshold)
        {
            return selected;
        }

        long mergedContentBytes = 0;
        var mergedDocuments = 0;
        foreach (var item in ordered.OrderBy(item => item.Entry.ContentBytes)
                     .ThenBy(item => item.Entry.LiveDocumentCount)
                     .ThenBy(item => item.Entry.SegmentId))
        {
            if (selected.Count >= options.MaxMergedSegments ||
                item.Entry.LiveDocumentCount == 0 ||
                item.Entry.ContentBytes > _options.TargetSegmentContentBytes - mergedContentBytes ||
                item.Entry.LiveDocumentCount > _options.TargetSegmentDocumentCount - mergedDocuments)
            {
                continue;
            }

            var segmentBytes = _storage.GetSegmentBytes(item.Entry.SegmentId);
            if (segmentBytes > options.MaxInputSegmentBytes - selectedSegmentBytes)
            {
                continue;
            }

            selected.Add(item.Entry.SegmentId);
            selectedSegmentBytes += segmentBytes;
            mergedContentBytes += item.Entry.ContentBytes;
            mergedDocuments += item.Entry.LiveDocumentCount;
        }

        if (selected.Count < 2)
        {
            selected.Clear();
        }

        return selected;
    }

    private bool HasEstimatedCompactionCapacity(
        CorpusManifest manifest,
        IReadOnlySet<ulong> selectedSegmentIds,
        CompactOptions options,
        CancellationToken cancellationToken)
    {
        var availableBytes = _storage.GetAvailableBytes();
        if (availableBytes is null)
        {
            return true;
        }

        long estimatedReplacementBytes = 0;
        foreach (var entry in manifest.Segments)
        {
            if (!selectedSegmentIds.Contains(entry.SegmentId) || entry.LiveDocumentCount == 0)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var segmentBytes = _storage.GetSegmentBytes(entry.SegmentId);
            var liveContentBytes = entry.ContentBytes - GetDeadContentBytes(
                entry,
                currentLegacyKeys: null,
                cancellationToken);
            var proportionalBytes = entry.ContentBytes == 0
                ? segmentBytes
                : (long)Math.Ceiling((double)segmentBytes * liveContentBytes / entry.ContentBytes);
            estimatedReplacementBytes = SaturatingAdd(
                estimatedReplacementBytes,
                SaturatingAdd(proportionalBytes, EstimatedReplacementPackOverheadBytes));
        }

        return estimatedReplacementBytes <=
            availableBytes.Value - Math.Min(availableBytes.Value, options.MinimumFreeBytesAfterCompaction);

        static long SaturatingAdd(long left, long right)
        {
            return right > long.MaxValue - left ? long.MaxValue : left + right;
        }
    }

    private long GetDeadContentBytes(
        SegmentManifestEntry segment,
        ISet<LegacyMaintenanceKey>? currentLegacyKeys,
        CancellationToken cancellationToken)
    {
        if (segment.DeadOrdinals.Length == 0)
        {
            return 0;
        }

        if (segment.DeadContentBytes >= 0)
        {
            return segment.DeadContentBytes;
        }

        var key = new LegacyMaintenanceKey(
            segment.SegmentId,
            segment.DocumentCount,
            segment.ContentBytes,
            segment.DeadOrdinals.Length);
        currentLegacyKeys?.Add(key);
        if (_legacyDeadContentBytes.TryGetValue(key, out var cachedBytes))
        {
            return cachedBytes;
        }

        long deadContentBytes = 0;
        using var mappedSegment = _storage.OpenSegment(
            segment.SegmentId,
            cancellationToken);
        foreach (var deadOrdinal in segment.DeadOrdinals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            deadContentBytes = checked(
                deadContentBytes +
                mappedSegment.GetDocument(deadOrdinal).ContentLength);
        }

        return _legacyDeadContentBytes.GetOrAdd(key, deadContentBytes);
    }

    private readonly record struct LiveDocumentKey(
        ulong SegmentId,
        uint Ordinal,
        int ContentLength,
        long MTimeUtcTicks,
        ulong ContentHash64);

    private readonly record struct LegacyMaintenanceKey(
        ulong SegmentId,
        int DocumentCount,
        long ContentBytes,
        int DeadDocumentCount);

    private sealed class Utf8PathComparer : IComparer<byte[]>
    {
        public static Utf8PathComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            return left.AsSpan().SequenceCompareTo(right);
        }
    }

    private readonly record struct CompactionPriority(CompactionDocument Document);

    private sealed class CompactionPriorityComparer : IComparer<CompactionPriority>
    {
        public static CompactionPriorityComparer Instance { get; } = new();

        public int Compare(CompactionPriority left, CompactionPriority right)
        {
            // Segment lookup uses UTF-16 ordinal ordering, which differs from UTF-8 byte order.
            return MappedSegment.ComparePathOrdinal(
                left.Document.GetPathUtf8(),
                right.Document.GetPathUtf8());
        }
    }

    private sealed class CompactionCursor : IDisposable
    {
        private readonly uint[] _deadOrdinals;
        private CompactionDocument? _current;
        private int _deadIndex;
        private uint _nextOrdinal;

        public CompactionCursor(MappedSegment segment, uint[] deadOrdinals)
        {
            Segment = segment;
            _deadOrdinals = deadOrdinals;
        }

        public MappedSegment Segment { get; }
        public CompactionDocument Current =>
            _current ?? throw new InvalidOperationException("The cursor has not advanced to a document.");

        public bool MoveNext()
        {
            while (_nextOrdinal < Segment.DocumentCount)
            {
                var ordinal = _nextOrdinal++;
                while (_deadIndex < _deadOrdinals.Length && _deadOrdinals[_deadIndex] < ordinal)
                {
                    _deadIndex++;
                }

                if (_deadIndex < _deadOrdinals.Length && _deadOrdinals[_deadIndex] == ordinal)
                {
                    continue;
                }

                _current = new CompactionDocument(Segment, Segment.GetDocument(ordinal));
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            Segment.Dispose();
        }
    }

}

internal readonly record struct CompactionPublication(
    List<SegmentManifestEntry> Segments,
    int MergedDocuments,
    long ContentBytes);

internal readonly record struct CompactionDocument(
    MappedSegment Segment,
    SegmentDocument Document) : ISegmentBuildDocument
{
    public int LineCount => Document.LineCount;
    public ushort Flags => Document.Flags;
    public long MTimeUtcTicks => Document.MTimeUtcTicks;
    public ulong ContentHash64 => Document.ContentHash64;
    public int ContentLength => Document.ContentLength;

    public ReadOnlySpan<byte> GetPathUtf8()
    {
        var document = Document;
        return Segment.GetPathBytes(document);
    }

    public ReadOnlySpan<byte> GetContentBytes()
    {
        var document = Document;
        return Segment.GetContentBytes(document);
    }

    public ReadOnlySpan<byte> GetEncodedLineIndex()
    {
        var document = Document;
        return Segment.GetEncodedLineIndexBytes(document);
    }
}

