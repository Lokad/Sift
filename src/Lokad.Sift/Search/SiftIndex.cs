using System.Diagnostics;
using System.Numerics;
using Lokad.Sift.Indexer;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Search;
using Lokad.Sift.Storage;

namespace Lokad.Sift;

public sealed class SiftIndex : ISiftIndex
{
    private delegate ValueTask<StreamedDocuments> UpdateDocumentStreamer(
        IReadOnlySet<string> forbiddenPaths,
        RejectedUpsertPolicy rejectedUpsertPolicy,
        Action<List<StoredDocument>> preparePartition,
        Action<IReadOnlyList<StoredDocument>> publishPartition,
        CancellationToken cancellationToken);

    private readonly ISiftStorage _storage;
    private readonly IndexBuilder _builder;
    private readonly IndexCompactor _compactor;
    private readonly SiftOptions _options;
    private readonly IDisposable? _retainedIndexingWorkspaceLease;
    private readonly MutationCoordinator _mutations = new();
    private readonly CancellationTokenSource _reclamationCancellation = new();
    private readonly ManualResetEventSlim _reclamationIdle = new(initialState: true);
    private readonly CancellationTokenSource _validationCancellation = new();
    private readonly ManualResetEventSlim _validationsIdle = new(initialState: true);
    private readonly SemaphoreSlim _validationGate = new(1, 1);
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

    public SiftIndex(string rootPath)
        : this(rootPath, new SiftOptions())
    {
    }

    public SiftIndex(string rootPath, SiftOptions options)
        : this(new CorpusStorage(rootPath, options.MaximumPhysicalBytes), options)
    {
    }

    internal SiftIndex(ISiftStorage storage, SiftOptions options)
    {
        _options = options;
        SiftOptionsValidator.Validate(_options);
        _retainedIndexingWorkspaceLease = options.RetainIndexingWorkspace
            ? SegmentIndexBuilder.AcquireWorkspaceLease()
            : null;
        _storage = storage;
        _builder = new IndexBuilder(_storage, _options);
        _compactor = new IndexCompactor(_storage, _options);
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

    public static SiftIndex CreateInMemory()
    {
        return CreateInMemory(new SiftOptions());
    }

    public static SiftIndex CreateInMemory(SiftOptions options)
    {
        return new SiftIndex(new InMemorySiftStorage(), options);
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
            SiftWriteActivity.Build,
            async operationCancellation =>
            {
                if (!_options.MeasurePeakManagedMemory)
                {
                    return await build(operationCancellation).ConfigureAwait(false);
                }

                using var measurement = ManagedMemoryPeakMeasurement.Start();
                var stats = await build(operationCancellation).ConfigureAwait(false);
                var peakManagedBytes = await measurement.CompleteAsync().ConfigureAwait(false);
                return stats.WithPeakManagedBytes(peakManagedBytes);
            },
            cancellationToken);
    }

    private async Task<TResult> RunMutation<TResult>(
        SiftWriteActivity activity,
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

    public WriteActivityStats GetWriteActivityStats()
    {
        ThrowIfDisposed();
        return _mutations.GetWriteActivityStats();
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
                return ValidateIndex(operationCancellation.Token, validationRevision);
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

        IndexValidationStats ValidateIndex(
            CancellationToken cancellationToken,
            long validationRevision)
        {
            long GetSegmentBytes(SegmentId segmentId)
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

            void ValidateSegment(SegmentId segmentId)
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
            using var snapshot = _storage.OpenReadSnapshotForValidation();
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
    }

    public MaintenanceStats GetMaintenanceStats()
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        try
        {
            return _compactor.GetMaintenanceStats();
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }

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
        IndexCompactor.ValidateOptions(options);
        try
        {
            return _compactor.ShouldCompact(options, ThrowIfRebuildRequired);
        }
        catch (Exception exception) when (IsIndexCorruption(exception))
        {
            MarkRequiresRebuild(exception);
            throw;
        }
    }

    public Task<UpdateStats> Update(
        IDocumentSource upserts,
        IReadOnlyList<DocumentKey> deletions,
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
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
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
        IReadOnlyList<DocumentKey> deletions,
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
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
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

    internal Task<UpdateStats> UpdatePrevalidated(
        LoadedDocuments upserts,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        return RunUpdate(
            (forbiddenPaths, _, preparePartition, publishPartition, operationCancellation) =>
                new ValueTask<StreamedDocuments>(IndexBuilder.StreamLoadedDocuments(
                    upserts,
                    operationCancellation,
                    _options,
                    forbiddenPaths,
                    collectAcceptedPaths: true,
                    preparePartition,
                    publishPartition)),
            deletions,
            options,
            cancellationToken);
    }

    private Task<UpdateStats> RunUpdate(
        UpdateDocumentStreamer streamDocuments,
        IReadOnlyList<DocumentKey> deletions,
        UpdateOptions options,
        CancellationToken callerCancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        ValidateUpdateOptions(options);
        return RunMutation(
            SiftWriteActivity.Update,
            async operationCancellation =>
            {
                try
                {
                    return await ApplyUpdate(operationCancellation).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsIndexCorruption(exception))
                {
                    MarkRequiresRebuild(exception);
                    throw;
                }
            },
            callerCancellationToken);

        static void ValidateUpdateOptions(UpdateOptions options)
        {
            if (!Enum.IsDefined(options.RejectedUpsertPolicy))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "RejectedUpsertPolicy is invalid.");
            }
        }

        async ValueTask<UpdateStats> ApplyUpdate(CancellationToken cancellationToken)
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
            using var writeSession = _storage.AcquireWriteSession(cancellationToken);
            ThrowIfRebuildRequired();
            var currentManifest = writeSession.CurrentManifest;
            var hadManifest = currentManifest is not null;
            var actualIdentity = currentManifest is null ? default : CreateIndexIdentity(currentManifest);
            var committedIdentity = actualIdentity;
            if (options.ExpectedIdentity is { } expectedIdentity &&
                (expectedIdentity.CorpusId != actualIdentity.CorpusId ||
                 expectedIdentity.ContentRevision != actualIdentity.ContentRevision))
            {
                throw new StaleIndexStateException(expectedIdentity, actualIdentity);
            }

            var existingManifest = currentManifest ?? new CorpusManifest(
                CorpusId.New(),
                ContentRevision: default,
                Generation: default,
                CommitMetadata: [],
                Segments: []);

            var deletionSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var deletion in deletions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = deletion.Path;
                if (!deletionSet.Add(normalized))
                {
                    throw new InvalidOperationException($"Duplicate deletion path in update batch: '{normalized}'.");
                }
            }

            var publishedSegments = new List<SegmentManifestEntry>();
            var attemptedSegmentIds = new List<SegmentId>();
            var existingUpsertKeys = new Dictionary<string, LiveDocumentKey>(StringComparer.Ordinal);
            var deletedDocuments = 0;
            StreamedDocuments upsertLoad;
            RejectedDocument[] rejectedDocuments;
            try
            {
                var segmentSeed = existingManifest.Generation.Next().Value ^ ((ulong)DateTime.UtcNow.Ticks << 1);
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
                        var segmentId = new SegmentId(segmentSeed + (ulong)publishedSegments.Count);
                        attemptedSegmentIds.Add(segmentId);
                        publishedSegments.Add(writeSession.PublishSegment(
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
                rejectedDocuments = upsertLoad.RejectedDocuments.ToArray();
                var pendingStats = new UpdateStats(
                    submittedUpserts: upsertLoad.Submitted,
                    upsertedDocuments: upsertLoad.Accepted,
                    skippedUpserts: upsertLoad.Skipped,
                    rejectedUpserts: upsertLoad.Rejected,
                    deletedDocuments: deletedDocuments,
                    publishedSegments: publishedSegments.Count,
                    submittedBytes: upsertLoad.SubmittedBytes,
                    contentBytes: upsertLoad.AcceptedBytes,
                    skippedBytes: upsertLoad.SkippedBytes,
                    rejectedBytes: upsertLoad.RejectedBytes,
                    rejectedDocumentDetails: rejectedDocuments,
                    elapsedMilliseconds: watch.ElapsedMilliseconds,
                    committedIdentity: default);
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
                    var newlyDeadBySegment = new Dictionary<SegmentId, List<LiveDocumentKey>>();

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
                        contentChanged
                            ? existingManifest.ContentRevision.Next()
                            : existingManifest.ContentRevision,
                        existingManifest.Generation.Next(),
                        commitMetadata.ToArray(),
                        newSegments);
                    ResetValidationStats();
                    writeSession.PublishManifest(manifest);
                    ResetValidationStats();
                    committedIdentity = CreateIndexIdentity(manifest);
                }
            }
            catch
            {
                writeSession.CleanupFailedMutation(attemptedSegmentIds);
                throw;
            }

            watch.Stop();
            return new UpdateStats(
                submittedUpserts: upsertLoad.Submitted,
                upsertedDocuments: upsertLoad.Accepted,
                skippedUpserts: upsertLoad.Skipped,
                rejectedUpserts: upsertLoad.Rejected,
                deletedDocuments: deletedDocuments,
                publishedSegments: publishedSegments.Count,
                submittedBytes: upsertLoad.SubmittedBytes,
                contentBytes: upsertLoad.AcceptedBytes,
                skippedBytes: upsertLoad.SkippedBytes,
                rejectedBytes: upsertLoad.RejectedBytes,
                rejectedDocumentDetails: rejectedDocuments,
                elapsedMilliseconds: watch.ElapsedMilliseconds,
                committedIdentity: committedIdentity);
        }
    }

    private static IndexIdentity CreateIndexIdentity(CorpusManifest manifest)
    {
        return new IndexIdentity(
            manifest.CorpusId,
            manifest.ContentRevision,
            manifest.Generation);
    }

    public Task<CompactStats> Compact(CompactOptions options, CancellationToken callerCancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfRebuildRequired();
        IndexCompactor.ValidateOptions(options);
        return RunMutation(
            SiftWriteActivity.Compact,
            async operationCancellation =>
            {
                try
                {
                    if (!_options.MeasurePeakManagedMemory)
                    {
                        return _compactor.Compact(
                            options,
                            ThrowIfRebuildRequired,
                            ResetValidationStats,
                            operationCancellation);
                    }

                    using var measurement = ManagedMemoryPeakMeasurement.Start();
                    var stats = _compactor.Compact(
                        options,
                        ThrowIfRebuildRequired,
                        ResetValidationStats,
                        operationCancellation);
                    var peakManagedBytes = await measurement.CompleteAsync().ConfigureAwait(false);
                    return stats.WithPeakManagedBytes(peakManagedBytes);
                }
                catch (Exception exception) when (IsIndexCorruption(exception))
                {
                    MarkRequiresRebuild(exception);
                    throw;
                }
            },
            callerCancellationToken);
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
        SegmentId failedSegmentId,
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
        IStorageWriteSession writeSession;
        try
        {
            writeSession = _storage.AcquireWriteSession(cancellationToken);
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

        using var _ = writeSession;
        Exception? currentFailure = null;
        try
        {
            if (writeSession.CurrentManifest is not { } currentManifest ||
                currentManifest.Segments.All(segment => segment.SegmentId != failedSegmentId))
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

    private sealed class ValidationScope(SiftIndex owner) : IDisposable
    {
        private SiftIndex? _owner = owner;

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

    private readonly record struct LiveDocumentKey(
        SegmentId SegmentId,
        uint Ordinal,
        int ContentLength,
        long MTimeUtcTicks,
        ulong ContentHash64);

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

}

