using System.Buffers.Binary;
using System.Diagnostics;
using Lokad.Sift;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Tests;

public sealed class StorageLifecycleTests
{
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

        using var writerLock = storage.AcquireWriterLock();
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

        var afterRelease = index.GetStorageStats();
        Assert.Equal(afterRelease.ManifestSegmentCount, afterRelease.PhysicalSegmentCount);
        Assert.Equal(afterRelease.ManifestSegmentBytes, afterRelease.PhysicalSegmentBytes);
        Assert.Equal(0, afterRelease.RetiredSegmentCount);
        Assert.Equal(0, afterRelease.RetiredSegmentBytes);
        Assert.Equal(0, afterRelease.LeasedGenerationCount);
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
        await secondIndex.Compact(new CompactOptions(SegmentCountTrigger: 1));

        var whileLeased = secondIndex.GetStorageStats();
        Assert.True(whileLeased.RetiredSegmentCount > 0);
        Assert.True(whileLeased.RetiredSegmentBytes > 0);

        var oldHits = new HitBuffer();
        oldSnapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), oldHits);
        Assert.Single(oldHits.Hits);

        oldSnapshot.Dispose();

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

            await index.Compact(new CompactOptions(SegmentCountTrigger: 1));
            var whileLeased = index.GetStorageStats();
            Assert.True(whileLeased.LeasedGenerationCount > 0);
            Assert.True(whileLeased.SnapshotDeferredSegmentBytes > 0);

            process.Kill(entireProcessTree: true);
            process.WaitForExit();

            await WaitUntilAsync(
                () => index.GetStorageStats().RetiredSegmentCount == 0,
                TimeSpan.FromSeconds(5));
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

        var leaseBytes = File.ReadAllBytes(leasePath);
        var corruptLeaseBytes = leaseBytes.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(corruptLeaseBytes.AsSpan(36), int.MaxValue);
        File.WriteAllBytes(leasePath, corruptLeaseBytes);

        var blocked = index.GetStorageStats();
        Assert.True(blocked.IsReclamationBlocked);
        Assert.True(File.Exists(orphanPack));
        Assert.Throws<InvalidDataException>(() => index.OpenSnapshot());

        File.WriteAllBytes(leasePath, leaseBytes);
        Assert.False(index.GetStorageStats().IsReclamationBlocked);
        Assert.False(File.Exists(orphanPack));
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
            async () => await index.Compact(new CompactOptions(SegmentCountTrigger: 1)));
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
    public async Task CorruptManifestPreventsOrphanDeletion()
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

        using (var stream = new FileStream(storage.ManifestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = stream.Length - 1;
            var value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte(unchecked((byte)(value ^ 0x5A)));
        }

        Assert.Throws<InvalidDataException>(() =>
        {
            _ = new SiftIndex(fixture.CorpusPath);
        });
        Assert.True(File.Exists(orphanPack));
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
        Assert.Equal(16, options.SegmentCountTrigger);
        Assert.Equal(1024, options.MetadataBloatDeadOrdinalsThreshold);
        Assert.Equal(1L * 1024 * 1024 * 1024, options.MaxInputSegmentBytes);

        using var index = SiftIndex.CreateInMemory();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = index.Compact(new CompactOptions(MaxMergedSegments: 0));
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = index.Compact(new CompactOptions(MaxInputSegmentBytes: 0));
        });
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

        var compact = await index.Compact(new CompactOptions(
            DeadFractionThreshold: 0,
            MaxInputSegmentBytes: 1));

        Assert.Equal(1, compact.InputSegments);
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
