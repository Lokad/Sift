using System.Buffers.Binary;
using System.Diagnostics;
using Lokad.Sift;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Tests;

public sealed class StorageLifecycleTests
{
    [Fact]
    public async Task IndexIdentityPersistsAcrossMutationsAndReopen()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        IndexIdentity updatedIdentity;
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            Assert.Equal(
                new IndexIdentity(CorpusId.Empty, new ContentRevision(0), new StorageGeneration(0)),
                index.GetIndexIdentity());
            await index.Build(fixture.CreateSource());
            var builtIdentity = index.GetIndexIdentity();
            Assert.NotEqual(CorpusId.Empty, builtIdentity.CorpusId);
            Assert.Equal(new ContentRevision(1), builtIdentity.ContentRevision);
            Assert.Equal(new StorageGeneration(1), builtIdentity.StorageGeneration);

            await index.Update(
                new InMemoryDocumentSource([("src/b.txt", "beta\n")]),
                []);
            updatedIdentity = index.GetIndexIdentity();
            Assert.Equal(builtIdentity.CorpusId, updatedIdentity.CorpusId);
            Assert.Equal(
                builtIdentity.ContentRevision.Value + 1,
                updatedIdentity.ContentRevision.Value);
            Assert.True(
                updatedIdentity.StorageGeneration.Value >
                builtIdentity.StorageGeneration.Value);
        }

        using var reopened = new SiftIndex(fixture.CorpusPath);
        Assert.Equal(updatedIdentity, reopened.GetIndexIdentity());
    }

    [Fact]
    public async Task FullBuildStartsNewLogicalCorpusEpoch()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        var first = index.GetIndexIdentity();
        await index.Update(new InMemoryDocumentSource([("src/b.txt", "beta")]), []);

        await index.Build(new InMemoryDocumentSource([("src/c.txt", "gamma")]));
        var rebuilt = index.GetIndexIdentity();

        Assert.NotEqual(first.CorpusId, rebuilt.CorpusId);
        Assert.Equal(new ContentRevision(1), rebuilt.ContentRevision);
        Assert.True(rebuilt.StorageGeneration.Value > first.StorageGeneration.Value);
    }

    [Fact]
    public async Task SnapshotRetainsItsCapturedIdentityAcrossPublication()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        using var snapshot = index.OpenSnapshot();
        var capturedIdentity = snapshot.Identity;

        await index.Update(new InMemoryDocumentSource([("src/b.txt", "beta")]), []);

        Assert.Equal(capturedIdentity, snapshot.Identity);
        Assert.NotEqual(index.GetIndexIdentity(), snapshot.Identity);
    }

    [Fact]
    public async Task CommitMetadataIsAtomicAndSurvivesCompactionAndReopen()
    {
        using var fixture = new CorpusFixture();
        IndexState compactedState;
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(
                new InMemoryDocumentSource([("src/a.txt", "alpha")]),
                new BuildOptions
                {
                    CommitMetadataProvider = stats =>
                    {
                        Assert.Equal(1, stats.IndexedDocuments);
                        return "initial"u8.ToArray();
                    }
                });
            var builtState = index.GetIndexState();
            Assert.Equal("initial"u8.ToArray(), builtState.CommitMetadata.ToArray());
            using (var snapshot = index.OpenSnapshot())
            {
                Assert.Equal(builtState.Identity, snapshot.Identity);
                Assert.Equal(builtState.CommitMetadata.ToArray(), snapshot.CommitMetadata.ToArray());
            }

            await index.Update(
                new InMemoryDocumentSource([]),
                [],
                new UpdateOptions
                {
                    CommitMetadataProvider = stats =>
                    {
                        Assert.Equal(0, stats.UpsertedDocuments);
                        return "advanced"u8.ToArray();
                    }
                });
            var metadataOnlyState = index.GetIndexState();
            Assert.Equal(builtState.Identity.ContentRevision, metadataOnlyState.Identity.ContentRevision);
            Assert.True(
                metadataOnlyState.Identity.StorageGeneration.Value >
                builtState.Identity.StorageGeneration.Value);
            Assert.Equal("advanced"u8.ToArray(), metadataOnlyState.CommitMetadata.ToArray());
            using (var snapshot = index.OpenSnapshot())
            {
                Assert.Equal(metadataOnlyState.Identity, snapshot.Identity);
                Assert.Equal(metadataOnlyState.CommitMetadata.ToArray(), snapshot.CommitMetadata.ToArray());
            }

            await index.Update(new InMemoryDocumentSource([("src/b.txt", "beta")]), []);
            var updatedState = index.GetIndexState();
            Assert.Equal(
                metadataOnlyState.Identity.ContentRevision.Value + 1,
                updatedState.Identity.ContentRevision.Value);
            Assert.Equal("advanced"u8.ToArray(), updatedState.CommitMetadata.ToArray());

            var compact = await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
            Assert.True(compact.InputSegments > 0);
            compactedState = index.GetIndexState();
            Assert.Equal(updatedState.Identity.ContentRevision, compactedState.Identity.ContentRevision);
            Assert.True(
                compactedState.Identity.StorageGeneration.Value >
                updatedState.Identity.StorageGeneration.Value);
            Assert.Equal("advanced"u8.ToArray(), compactedState.CommitMetadata.ToArray());
        }

        using var reopened = new SiftIndex(fixture.CorpusPath);
        var reopenedState = reopened.GetIndexState();
        Assert.Equal(compactedState.Identity, reopenedState.Identity);
        Assert.Equal(compactedState.CommitMetadata.ToArray(), reopenedState.CommitMetadata.ToArray());
    }

    [Fact]
    public async Task CommitMetadataProviderFailureDoesNotPublishUpdate()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        var before = index.GetIndexState();

        await Assert.ThrowsAsync<InvalidOperationException>(() => index.Update(
            new InMemoryDocumentSource([("src/b.txt", "beta")]),
            [],
            new UpdateOptions
            {
                CommitMetadataProvider = _ => throw new InvalidOperationException("checkpoint failed")
            }));

        Assert.Equal(before.Identity, index.GetIndexState().Identity);
        using var snapshot = index.OpenSnapshot();
        Assert.False(snapshot.TryGetDocumentBytes(new DocumentKey("src/b.txt"), out _));
    }

    [Fact]
    public void WriterLockRetriesOnlySharingViolations()
    {
        Assert.True(CorpusStorage.IsWriterLockContention(
            new IOException("sharing violation", unchecked((int)0x80070020))));
        Assert.True(CorpusStorage.IsWriterLockContention(
            new IOException("resource temporarily unavailable", unchecked((int)0x8007000B))));
        Assert.False(CorpusStorage.IsWriterLockContention(
            new IOException("disk full", unchecked((int)0x80070070))));
    }

    [Fact]
    public async Task SnapshotOpenDoesNotUseWriterLockOrCreateLeaseFiles()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var storage = TestHelpers.GetStorage(index);
        var leaseFilesBefore = Directory.EnumerateFiles(storage.GenerationLeasesPath, "*.lease").Count();

        using var writeSession = storage.AcquireWriteSession(CancellationToken.None);
        var openTask = Task.Run(() => index.OpenSnapshot());
        var completed = await Task.WhenAny(openTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(openTask, completed);
        using var snapshot = await openTask;
        for (var i = 0; i < 100; i++)
        {
            using var churnSnapshot = index.OpenSnapshot();
        }

        var leaseFilesAfter = Directory.EnumerateFiles(storage.GenerationLeasesPath, "*.lease").Count();
        Assert.Equal(leaseFilesBefore, leaseFilesAfter);
    }

    [Fact]
    public async Task SnapshotMetadataRetriesWhenPublicationRetiresObservedLease()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var probingStorage = new CorpusStorage(fixture.CorpusPath);
        var published = false;
        probingStorage.SnapshotMetadataLeaseOpening = () =>
        {
            if (published)
            {
                return;
            }

            published = true;
            index.Update(
                new InMemoryDocumentSource([("src/b.txt", "beta\n")]),
                []).GetAwaiter().GetResult();
        };

        ((ISiftStorage)probingStorage).EnsureSnapshotMetadata();

        Assert.True(published);
        using var snapshot = index.OpenSnapshot();
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("src/b.txt"), out _));
    }

    [Fact]
    public async Task FullBuildRetiresOldPacksUntilSnapshotIsReleased()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        using var oldSnapshot = index.OpenSnapshot();

        await index.Build(new InMemoryDocumentSource([("src/a.txt", "omega\n")]));

        var whileLeased = index.GetStorageStats();
        Assert.Equal(1, whileLeased.ManifestSegmentCount);
        Assert.Equal(2, whileLeased.PhysicalSegmentCount);
        Assert.Equal(1, whileLeased.RetiredSegmentCount);
        Assert.Equal(1, whileLeased.LeasedGenerationCount);
        Assert.Equal(whileLeased.RetiredSegmentBytes, whileLeased.SnapshotDeferredSegmentBytes);

        var oldHits = new HitBuffer();
        oldSnapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), oldHits);
        Assert.Single(oldHits.Hits);

        oldSnapshot.Dispose();

        await WaitUntilAsync(
            () => index.GetStorageStats().RetiredSegmentCount == 0,
            TimeSpan.FromSeconds(5));
        var afterRelease = index.GetStorageStats();
        Assert.Equal(afterRelease.ManifestSegmentCount, afterRelease.PhysicalSegmentCount);
        Assert.Equal(afterRelease.ManifestSegmentBytes, afterRelease.PhysicalSegmentBytes);
        Assert.Equal(0, afterRelease.RetiredSegmentCount);
        Assert.Equal(0, afterRelease.RetiredSegmentBytes);
        Assert.Equal(0, afterRelease.LeasedGenerationCount);
    }

    [Fact]
    public async Task StorageStatsDoNotReclaimRetiredPacks()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        var storage = new CorpusStorage(fixture.CorpusPath);
        using var index = new SiftIndex(storage, new SiftOptions());
        await index.Build(fixture.CreateSource());
        var oldSnapshot = ((ISiftStorage)storage).OpenReadSnapshot();

        await index.Build(new InMemoryDocumentSource([("src/a.txt", "omega\n")]));
        oldSnapshot.Dispose();

        var observed = index.GetStorageStats();

        Assert.Equal(1, observed.RetiredSegmentCount);
        Assert.Equal(1, observed.PendingDeletionSegmentCount);
        ((ISiftStorage)storage).ReclaimUnreferencedSegments(CancellationToken.None);
        Assert.Equal(0, index.GetStorageStats().RetiredSegmentCount);
    }

    [Fact]
    public async Task SnapshotLeaseProtectsPacksAcrossSiftInstances()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using var firstIndex = new SiftIndex(fixture.CorpusPath);
        await firstIndex.Build(fixture.CreateSource());
        await firstIndex.Update(
            new InMemoryDocumentSource([("src/a.txt", "omega\n")]),
            [new DocumentKey("src/b.txt")]);
        using var oldSnapshot = firstIndex.OpenSnapshot();

        using var secondIndex = new SiftIndex(fixture.CorpusPath);
        await secondIndex.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });

        var whileLeased = secondIndex.GetStorageStats();
        Assert.True(whileLeased.RetiredSegmentCount > 0);
        Assert.True(whileLeased.RetiredSegmentBytes > 0);

        var oldHits = new HitBuffer();
        oldSnapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), oldHits);
        Assert.Single(oldHits.Hits);

        oldSnapshot.Dispose();

        await WaitUntilAsync(
            () => secondIndex.GetStorageStats().RetiredSegmentCount == 0,
            TimeSpan.FromSeconds(5));
        var afterRelease = secondIndex.GetStorageStats();
        Assert.Equal(0, afterRelease.RetiredSegmentCount);
        Assert.Equal(afterRelease.ManifestSegmentBytes, afterRelease.PhysicalSegmentBytes);
    }

    [Fact]
    public async Task GenerationLeaseProtectsPacksAcrossProcessesAndRecoversAfterCrash()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(
            new InMemoryDocumentSource([("src/a.txt", "omega\n")]),
            [new DocumentKey("src/b.txt")]);

        var signalRoot = Path.Combine(Path.GetTempPath(), "sift-lease-signals", Guid.NewGuid().ToString("N"));
        var readyPath = Path.Combine(signalRoot, "ready");
        var releasePath = Path.Combine(signalRoot, "release");
        Directory.CreateDirectory(signalRoot);
        using var process = StartSnapshotHolder(fixture.CorpusPath, readyPath, releasePath);
        try
        {
            await WaitUntilAsync(() => File.Exists(readyPath), TimeSpan.FromSeconds(10));

            await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
            var whileLeased = index.GetStorageStats();
            Assert.True(whileLeased.LeasedGenerationCount > 0);
            Assert.True(whileLeased.SnapshotDeferredSegmentBytes > 0);

            process.Kill(entireProcessTree: true);
            process.WaitForExit();

            ((ISiftStorage)TestHelpers.GetStorage(index))
                .ReclaimUnreferencedSegments(CancellationToken.None);
            Assert.Equal(0, index.GetStorageStats().RetiredSegmentCount);
        }
        finally
        {
            if (!process.HasExited)
            {
                File.WriteAllText(releasePath, "release");
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            Directory.Delete(signalRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DefaultCompactionDeletesReplacedPacksImmediately()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(
            new InMemoryDocumentSource([("src/a.txt", "omega\n")]),
            [new DocumentKey("src/b.txt")]);

        var compact = await index.Compact(new CompactOptions());

        Assert.Equal(1, compact.InputSegments);
        var storage = index.GetStorageStats();
        Assert.Equal(storage.ManifestSegmentCount, storage.PhysicalSegmentCount);
        Assert.Equal(storage.ManifestSegmentBytes, storage.PhysicalSegmentBytes);
        Assert.Equal(0, storage.RetiredSegmentCount);
    }

    [Fact]
    public async Task ReplacementIsRefusedWhileRetiredGenerationIsLeased()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        using var oldSnapshot = index.OpenSnapshot();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "omega\n")]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await index.Build(new InMemoryDocumentSource([("src/a.txt", "theta\n")])));

        oldSnapshot.Dispose();
        await WaitUntilAsync(
            () => index.GetStorageStats().RetiredSegmentCount == 0,
            TimeSpan.FromSeconds(5));

        var build = await index.Build(new InMemoryDocumentSource([("src/a.txt", "theta\n")]));
        Assert.Equal(1, build.IndexedDocuments);
    }

    [Fact]
    public async Task CleanupFailureAfterManifestSwapDoesNotFailUpdate()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var storage = TestHelpers.GetStorage(index);
        var invalidLeasePath = Path.Combine(storage.GenerationLeasesPath, "FFFFFFFFFFFFFFFE.lease");
        File.WriteAllText(invalidLeasePath, "invalid");

        using (new FileStream(invalidLeasePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var update = await index.Update(
                new InMemoryDocumentSource([("src/a.txt", "omega\n")]),
                []);

            Assert.Equal(1, update.UpsertedDocuments);
            Assert.True(index.GetStorageStats().IsReclamationBlocked);
        }

        File.Delete(invalidLeasePath);
        Assert.False(index.GetStorageStats().IsReclamationBlocked);
    }

    [Fact]
    public async Task CorruptCurrentGenerationLeaseFailsClosed()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var storage = TestHelpers.GetStorage(index);
        var manifest = storage.ReadManifest();
        var leasePath = Path.Combine(
            storage.GenerationLeasesPath,
            manifest.Generation.ToString("X16") + ".lease");
        var sourcePack = Path.Combine(
            storage.SegmentsPath,
            manifest.Segments[0].SegmentId.ToString("X16"),
            "segment.pack");
        var orphanDirectory = Path.Combine(storage.SegmentsPath, "FFFFFFFFFFFFFFFD");
        var orphanPack = Path.Combine(orphanDirectory, "segment.pack");
        Directory.CreateDirectory(orphanDirectory);
        File.Copy(sourcePack, orphanPack);

        var corruptLeaseBytes = File.ReadAllBytes(leasePath);
        BinaryPrimitives.WriteInt32LittleEndian(corruptLeaseBytes.AsSpan(36), int.MaxValue);
        File.WriteAllBytes(leasePath, corruptLeaseBytes);

        var blocked = index.GetStorageStats();
        Assert.True(blocked.IsReclamationBlocked);
        Assert.True(File.Exists(orphanPack));
        Assert.Throws<InvalidDataException>(() => index.OpenSnapshot());
        Assert.True(index.RequiresRebuild);

        await index.Build(fixture.CreateSource());

        Assert.False(index.RequiresRebuild);
        Assert.False(File.Exists(orphanPack));
        Assert.False(index.GetStorageStats().IsReclamationBlocked);
    }

    [Fact]
    public async Task FailedManifestSwapImmediatelyReclaimsAttemptedPack()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(new InMemoryDocumentSource([("src/a.txt", "omega\n")]), []);

        var storage = TestHelpers.GetStorage(index);
        storage.FaultInjector = new PublicationFaultInjector
        {
            ThrowOnStage = PublicationStage.AfterManifestFlushBeforeSwap
        };

        await Assert.ThrowsAsync<IOException>(
            async () => await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 }));
        storage.FaultInjector = null;

        var manifestBeforeRestart = storage.ReadManifest();
        var packsBeforeRestart = Directory.EnumerateFiles(
            storage.SegmentsPath,
            "segment.pack",
            SearchOption.AllDirectories).Count();
        Assert.Equal(manifestBeforeRestart.Segments.Count, packsBeforeRestart);

        using var restartedIndex = new SiftIndex(fixture.CorpusPath);
        var afterRestart = restartedIndex.GetStorageStats();
        Assert.Equal(0, afterRestart.RetiredSegmentCount);
        Assert.Equal(afterRestart.ManifestSegmentBytes, afterRestart.PhysicalSegmentBytes);
    }

    [Fact]
    public async Task LegacyCorpusMigrationSchedulesOrphanReclamation()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        CorpusStorage storage;
        string orphanPack;
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
            storage = TestHelpers.GetStorage(index);
            var manifest = storage.ReadManifest();
            var sourcePack = Path.Combine(
                storage.SegmentsPath,
                manifest.Segments[0].SegmentId.ToString("X16"),
                "segment.pack");
            var orphanDirectory = Path.Combine(storage.SegmentsPath, "FFFFFFFFFFFFFFFC");
            orphanPack = Path.Combine(orphanDirectory, "segment.pack");
            Directory.CreateDirectory(orphanDirectory);
            File.Copy(sourcePack, orphanPack);
        }

        Directory.Delete(storage.GenerationLeasesPath, recursive: true);
        File.Delete(Path.Combine(storage.LockPath, "reclamation.state"));

        using var migratedIndex = new SiftIndex(fixture.CorpusPath);
        await WaitUntilAsync(() => !File.Exists(orphanPack), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReclamationQueueRejectionDoesNotStrandIndexDisposal()
    {
        var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = 1
        });
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        var oldSnapshot = index.OpenSnapshot();
        await index.Update(new InMemoryDocumentSource([("src/a.txt", "omega")]), []);
        var schedulingAttempts = 0;
        index.ReclamationWorkItemScheduler = _ =>
        {
            schedulingAttempts++;
            return false;
        };

        oldSnapshot.Dispose();

        Assert.Equal(1, schedulingAttempts);
        await Task.Run(index.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CorruptManifestRequiresExplicitFullRebuild()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var storage = TestHelpers.GetStorage(index);
        var manifest = storage.ReadManifest();
        var sourcePack = Path.Combine(
            storage.SegmentsPath,
            manifest.Segments[0].SegmentId.ToString("X16"),
            "segment.pack");
        var orphanDirectory = Path.Combine(storage.SegmentsPath, "FFFFFFFFFFFFFFFE");
        var orphanPack = Path.Combine(orphanDirectory, "segment.pack");
        Directory.CreateDirectory(orphanDirectory);
        File.Copy(sourcePack, orphanPack);

        CorruptLastByte(storage.ManifestPath);

        using var reopened = new SiftIndex(fixture.CorpusPath);
        Assert.True(reopened.RequiresRebuild);
        Assert.Throws<InvalidDataException>(() => reopened.OpenSnapshot());
        Assert.True(File.Exists(orphanPack));

        await reopened.Build(fixture.CreateSource());

        Assert.False(reopened.RequiresRebuild);
        Assert.False(File.Exists(orphanPack));
        using var snapshot = reopened.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            collector);
        Assert.Single(collector.Hits);
    }

    [Fact]
    public async Task CorruptionDiscoveredByBuildRequiresRecovery()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var manifestPath = TestHelpers.GetStorage(index).ManifestPath;
        var lastWriteTime = File.GetLastWriteTimeUtc(manifestPath);
        CorruptLastByte(manifestPath);
        File.SetLastWriteTimeUtc(manifestPath, lastWriteTime);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            index.Build(new InMemoryDocumentSource([("src/b.txt", "beta\n")])));
        Assert.True(index.RequiresRebuild);
        Assert.Throws<InvalidDataException>(() => index.OpenSnapshot());

        await index.Build(new InMemoryDocumentSource([("src/b.txt", "beta\n")]));

        Assert.False(index.RequiresRebuild);
        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(
            new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            collector);
        Assert.Single(collector.Hits);
    }

    [Fact]
    public async Task InterruptedRecoveryCannotAppearAsAnEmptyIndex()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        var markerPath = Path.Combine(fixture.CorpusPath, "lock", "rebuild.required");
        File.WriteAllBytes(markerPath, [1]);

        using var reopened = new SiftIndex(fixture.CorpusPath);
        Assert.True(reopened.RequiresRebuild);
        Assert.Throws<InvalidDataException>(() => reopened.OpenSnapshot());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.Build(new InMemoryDocumentSource(
            [
                ("src/duplicate.txt", "first\n"),
                ("src/duplicate.txt", "second\n")
            ])));
        Assert.True(reopened.RequiresRebuild);
        Assert.True(File.Exists(markerPath));

        await reopened.Build(fixture.CreateSource());

        Assert.False(reopened.RequiresRebuild);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task InMemoryBuildReclaimsReplacedSegments()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha\n")]));
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "omega\n")]));

        var stats = index.GetStorageStats();
        Assert.Equal(1, stats.ManifestSegmentCount);
        Assert.Equal(1, stats.PhysicalSegmentCount);
        Assert.Equal(0, stats.RetiredSegmentCount);
    }

    [Fact]
    public void CompactOptionsHaveSafeDefaultsAndRejectInvalidValues()
    {
        var options = new CompactOptions();

        Assert.Equal(16, options.MaxMergedSegments);
        Assert.Equal(0.20, options.DeadFractionThreshold);
        Assert.Equal(0.20, options.DeadContentFractionThreshold);
        Assert.Equal(16, options.MinimumSegmentCountForSmallMerge);
        Assert.Equal(1024, options.MetadataBloatDeadOrdinalsThreshold);
        Assert.Equal(1L * 1024 * 1024 * 1024, options.MaxInputSegmentBytes);
        Assert.Equal(512L * 1024 * 1024, options.MinimumFreeBytesAfterCompaction);

        using var index = SiftIndex.CreateInMemory();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = index.Compact(new CompactOptions { MaxMergedSegments = 0 });
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = index.Compact(new CompactOptions { MaxInputSegmentBytes = 0 });
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = index.Compact(new CompactOptions { DeadContentFractionThreshold = double.NaN });
        });
    }

    [Fact]
    public void SiftOptionsRejectInvalidPhysicalByteBudgets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SiftIndex.CreateInMemory(new SiftOptions
            {
                MaximumPhysicalBytes = 0,
                MinimumFreeBytesAfterMutation = 0
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SiftIndex.CreateInMemory(new SiftOptions
            {
                MaximumPhysicalBytes = 1024,
                MinimumFreeBytesAfterMutation = 1024
            }));
    }

    [Fact]
    public void SiftOptionsRejectValuesOutsidePlannerAndSegmentRepresentations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SiftIndex.CreateInMemory(new SiftOptions
            {
                MaxSelectedNgrams = SiftOptions.MaximumSelectedNgramCount + 1
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SiftIndex.CreateInMemory(new SiftOptions
            {
                TargetSegmentContentBytes = long.MaxValue
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SiftIndex.CreateInMemory(new SiftOptions
            {
                TargetSegmentDocumentCount = int.MaxValue
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SiftIndex.CreateInMemory(new SiftOptions
            {
                MaxDocumentBytes = int.MaxValue
            }));
    }

    [Fact]
    public void MutationReserveCoversOneDefaultCompactionPass()
    {
        var options = new SiftOptions();
        var compact = new CompactOptions();

        Assert.True(options.MinimumFreeBytesAfterMutation >=
                    compact.MaxInputSegmentBytes + compact.MinimumFreeBytesAfterCompaction);
    }

    [Fact]
    public async Task CompactionInputBudgetLimitsEachMaintenancePass()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentContentBytes = 10
        });
        await index.Build(new InMemoryDocumentSource([
            ("src/a.txt", "0123456789"),
            ("src/b.txt", "0123456789"),
            ("src/c.txt", "0123456789")
        ]));

        var compact = await index.Compact(new CompactOptions { DeadFractionThreshold = 0, MaxInputSegmentBytes = 1 });

        Assert.Equal(1, compact.InputSegments);
    }

    [Fact]
    public async Task CompactionHintRequiresRoomForReplacementAndReserve()
    {
        using var fixture = new CorpusFixture(("src/a.txt", new string('a', 1024)));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            MaximumPhysicalBytes = 8L * 1024 * 1024,
            MinimumFreeBytesAfterMutation = 0
        });
        await index.Build(fixture.CreateSource());

        var storage = index.GetStorageStats();
        var availableBytes = Assert.IsType<long>(storage.AvailableBytes);
        var options = new CompactOptions
        {
            DeadFractionThreshold = 0,
            MinimumFreeBytesAfterCompaction = availableBytes
        };

        Assert.False(index.ShouldCompact(options));
        await Assert.ThrowsAsync<IOException>(() => index.Compact(options));
    }

    [Fact]
    public async Task CleanMaintenanceChecksLeaveReclamationMarkerUntouched()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var markerPath = Path.Combine(fixture.CorpusPath, "lock", "reclamation.state");
        var oldWriteTime = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(markerPath, oldWriteTime);
        var expectedWriteTime = File.GetLastWriteTimeUtc(markerPath);

        Assert.False(index.ShouldCompact(new CompactOptions()));
        _ = index.GetStorageStats();

        Assert.Equal(0, File.ReadAllBytes(markerPath).Single());
        Assert.Equal(expectedWriteTime, File.GetLastWriteTimeUtc(markerPath));
    }

    [Fact]
    public async Task CompactionReportsRemainingBoundedWork()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentContentBytes = 10
        });
        await index.Build(new InMemoryDocumentSource([
            ("src/a.txt", "0123456789"),
            ("src/b.txt", "0123456789"),
            ("src/c.txt", "0123456789")
        ]));
        await index.Update(
            new InMemoryDocumentSource([]),
            [
                new DocumentKey("src/a.txt"),
                new DocumentKey("src/b.txt"),
                new DocumentKey("src/c.txt")
            ]);
        var options = new CompactOptions { MaxMergedSegments = 1 };

        var first = await index.Compact(options);
        var second = await index.Compact(options);
        var third = await index.Compact(options);

        Assert.True(first.HasMoreWork);
        Assert.True(second.HasMoreWork);
        Assert.False(third.HasMoreWork);
    }

    [Fact]
    public async Task CompactionHintStopsWhilePriorGenerationIsLeased()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentContentBytes = 10
        });
        await index.Build(new InMemoryDocumentSource([
            ("src/a.txt", "0123456789"),
            ("src/b.txt", "0123456789"),
            ("src/c.txt", "0123456789")
        ]));
        await index.Update(
            new InMemoryDocumentSource([]),
            [
                new DocumentKey("src/a.txt"),
                new DocumentKey("src/b.txt"),
                new DocumentKey("src/c.txt")
            ]);
        using var oldSnapshot = index.OpenSnapshot();
        var options = new CompactOptions { MaxMergedSegments = 1 };

        var first = await index.Compact(options);

        Assert.False(first.HasMoreWork);
        Assert.False(index.ShouldCompact(options));
        await Assert.ThrowsAsync<InvalidOperationException>(() => index.Compact(options));

        oldSnapshot.Dispose();
        Assert.True(index.ShouldCompact(options));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                Assert.Fail($"Condition was not met within {timeout}.");
            }

            await Task.Delay(20);
        }
    }

    private static void CorruptLastByte(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = stream.Length - 1;
        var value = stream.ReadByte();
        stream.Position--;
        stream.WriteByte(unchecked((byte)(value ^ 0x5A)));
    }

    private static Process StartSnapshotHolder(string corpusPath, string readyPath, string releasePath)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .AncestorsAndSelf()
            .First(directory =>
                string.Equals(directory.Name, "Debug", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directory.Name, "Release", StringComparison.OrdinalIgnoreCase))
            .Name;
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var hostAssembly = Path.Combine(
            repositoryRoot,
            "tests",
            "Lokad.Sift.TestHost",
            "bin",
            configuration,
            "net10.0",
            "Lokad.Sift.TestHost.dll");
        Assert.True(File.Exists(hostAssembly), $"Expected test host assembly at '{hostAssembly}'.");

        var startInfo = new ProcessStartInfo(
            "dotnet",
            $"\"{hostAssembly}\" \"{corpusPath}\" \"{readyPath}\" \"{releasePath}\"")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Sift test host.");
    }
}

internal static class DirectoryInfoExtensions
{
    public static IEnumerable<DirectoryInfo> AncestorsAndSelf(this DirectoryInfo directory)
    {
        for (var current = directory; current is not null; current = current.Parent)
        {
            yield return current;
        }
    }
}
