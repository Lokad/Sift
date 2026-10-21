using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Lokad.Sift;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Search;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Tests;

[Collection("ProcessMetrics")]
public sealed class StorageIntegrityTests
{
    [Fact]
    public async Task OlderManifestRefreshCannotReplaceNewerStoredGeneration()
    {
        using var fixture = new CorpusFixture();
        Directory.CreateDirectory(fixture.CorpusPath);
        var path = Path.Combine(fixture.CorpusPath, "manifest-cache-test.bin");
        var first = new CorpusManifest(
            CorpusId.New(),
            new StorageGeneration(1),
            []);
        var second = new CorpusManifest(
            first.CorpusId,
            new StorageGeneration(2),
            []);
        File.WriteAllBytes(path, CorpusManifestCodec.Encode(first));
        var cache = new CorpusManifestCache();
        var refreshReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        cache.BeforeRefreshCacheStore = () =>
        {
            refreshReady.SetResult();
            releaseRefresh.Task.GetAwaiter().GetResult();
        };
        var olderRefresh = Task.Run(() => cache.Refresh(path));
        await refreshReady.Task;
        File.WriteAllBytes(path, CorpusManifestCodec.Encode(second));
        cache.Store(path, second);

        releaseRefresh.SetResult();
        Assert.Equal(first.CorpusId, (await olderRefresh).CorpusId);
        cache.BeforeRefreshCacheStore = null;
        var parseCount = cache.ParseCount;

        Assert.Same(second, cache.Read(path));
        Assert.Equal(parseCount, cache.ParseCount);
    }

    [Fact]
    public void SnapshotConstructionFailureReleasesGenerationLease()
    {
        var entry = new SegmentManifestEntry(
            new SegmentId(1),
            DocumentCount: 0,
            ContentBytes: 0,
            DeadOrdinalSet.Empty,
            DeadContentBytes: 0);
        var manifest = new CorpusManifest(
            CorpusId.New(),
            new StorageGeneration(1),
            [entry, entry]);
        var lease = new TrackingDisposable();
        var storage = new SnapshotOnlyStorage(manifest, lease);

        Assert.Throws<ArgumentException>(() => new SearchSnapshot(
            storage,
            new SiftOptions(),
            new SearchOptions(),
            onDisposed: null,
            onIndexFailure: null));

        Assert.True(lease.IsDisposed);
    }

    [Fact]
    public async Task InMemoryStorageSharesFrozenManifestAcrossReaders()
    {
        var storage = new InMemorySiftStorage();
        using var index = new SiftIndex(storage, new SiftOptions());
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha\n")]));

        var manifest = storage.ReadManifest();
        Assert.Same(manifest, storage.ReadManifest());
        using var first = ((ISiftStorage)storage).OpenReadSnapshot();
        using var second = ((ISiftStorage)storage).OpenReadSnapshot();
        Assert.Same(manifest, first.Manifest);
        Assert.Same(manifest, second.Manifest);
    }

    [Fact]
    public void PublishedManifestsOwnMutableInputs()
    {
        using var fixture = new CorpusFixture();

        AssertOwnsMutableInputs(new CorpusStorage(fixture.CorpusPath));
        AssertOwnsMutableInputs(new InMemorySiftStorage());

        static void AssertOwnsMutableInputs(ISiftStorage storage)
        {
            var commitMetadata = new byte[] { 7 };
            var deadOrdinals = new uint[] { 3 };
            var segments = new[]
            {
                new SegmentManifestEntry(new SegmentId(1), 4, 10, DeadOrdinalSet.FromOwned([.. deadOrdinals]), DeadContentBytes: 2)
            };
            using (var session = storage.AcquireWriteSession(CancellationToken.None))
            {
                session.PublishManifest(new CorpusManifest(
                    new CorpusId(Guid.NewGuid()),
                    ContentRevision: new ContentRevision(1),
                    Generation: new StorageGeneration(1),
                    CommitMetadata: commitMetadata,
                    Segments: segments), 0);
            }

            commitMetadata[0] = 9;
            deadOrdinals[0] = 9;
            segments[0] = segments[0] with { SegmentId = new SegmentId(2) };

            var published = storage.ReadManifest();
            Assert.Equal(7, published.CommitMetadata[0]);
            Assert.Equal(new SegmentId(1), published.Segments[0].SegmentId);
            Assert.Equal(3U, published.Segments[0].DeadOrdinals[0]);
        }
    }

    [Fact]
    public async Task InMemoryStorageStatsWaitForManifestPublication()
    {
        var storage = new BlockingPublicationStorage(new InMemorySiftStorage());
        using var index = new SiftIndex(storage, new SiftOptions());
        var buildTask = index.Build(
            new InMemoryDocumentSource([("src/a.txt", "alpha\n")]),
            new BuildOptions(),
            CancellationToken.None);

        Assert.True(storage.SegmentPublished.Wait(TimeSpan.FromSeconds(10)));
        var statsTask = Task.Run(index.GetStorageStats);

        try
        {
            var firstCompleted = await Task.WhenAny(statsTask, Task.Delay(TimeSpan.FromMilliseconds(100)));
            Assert.NotSame(statsTask, firstCompleted);
        }
        finally
        {
            storage.AllowManifestPublication.Set();
        }

        await buildTask;
        var stats = await statsTask;
        Assert.Equal(1, stats.ManifestSegmentCount);
        Assert.Equal(1, stats.PhysicalSegmentCount);
    }

    [Fact]
    public async Task SnapshotOpenReusesParsedManifestAndObservesExternalPublication()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha"));
        var firstStorage = new CorpusStorage(fixture.CorpusPath);
        using var first = new SiftIndex(firstStorage, new SiftOptions());
        await first.Build(fixture.CreateSource());

        var baselineParses = firstStorage.ManifestParseCount;
        for (var i = 0; i < 10; i++)
        {
            using var snapshot = first.OpenSnapshot();
        }

        Assert.Equal(baselineParses, firstStorage.ManifestParseCount);

        using (var second = new SiftIndex(fixture.CorpusPath))
        {
            await second.Update(new InMemoryDocumentSource([("src/b.txt", "beta")]), []);
        }

        using var refreshed = first.OpenSnapshot();
        Assert.True(refreshed.TryGetDocumentBytes(new DocumentKey("src/b.txt"), out _));
        Assert.Equal(baselineParses + 1, firstStorage.ManifestParseCount);
    }

    [Fact]
    public async Task MultiSegmentBuildScansCorpusCapacityOnce()
    {
        using var fixture = new CorpusFixture();
        var storage = new CorpusStorage(fixture.CorpusPath);
        using var index = new SiftIndex(storage, new SiftOptions
        {
            TargetSegmentDocumentCount = 1,
            MinimumFreeBytesAfterMutation = 0
        });
        var baselineScans = storage.CorpusPhysicalScanCount;
        var documents = Enumerable.Range(0, 8)
            .Select(static i => ($"src/{i}.txt", $"document {i}"))
            .ToArray();

        var build = await index.Build(new InMemoryDocumentSource(documents));

        Assert.Equal(8, build.SegmentCount);
        Assert.Equal(baselineScans + 1, storage.CorpusPhysicalScanCount);
    }

    [Fact]
    public async Task SegmentValidationObservesCancellation()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var storage = new CorpusStorage(fixture.CorpusPath);
        var segmentId = storage.ReadManifest().Segments[0].SegmentId;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            storage.OpenSegment(segmentId, cancellation.Token));
    }

    [Fact]
    public void AvailableBytesUsesMostSpecificMountedFileSystem()
    {
        var nestedDrive = DriveInfo.GetDrives()
            .Where(static drive => drive.IsReady)
            .Where(static drive =>
            {
                var root = drive.RootDirectory.FullName;
                return root.Length > (Path.GetPathRoot(root)?.Length ?? 0);
            })
            .OrderByDescending(static drive => drive.RootDirectory.FullName.Length)
            .FirstOrDefault();
        if (nestedDrive is null)
        {
            return;
        }

        var expectedBefore = nestedDrive.AvailableFreeSpace;
        var probePath = Path.Combine(
            nestedDrive.RootDirectory.FullName,
            $"sift-capacity-probe-{Guid.NewGuid():N}");
        var actual = new CorpusStorage(probePath).GetAvailableBytes();
        var expectedAfter = nestedDrive.AvailableFreeSpace;

        Assert.NotNull(actual);
        Assert.InRange(
            actual.Value,
            Math.Min(expectedBefore, expectedAfter),
            Math.Max(expectedBefore, expectedAfter));
    }

    [Fact]
    public async Task BuildPublishesManifestAndSearchableSegment()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\nbeta\n"),
            ("src/b.txt", "beta\ngamma\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        var stats = await index.Build(fixture.CreateSource());

        Assert.Equal(2, stats.IndexedDocuments);
        Assert.True(File.Exists(Path.Combine(fixture.CorpusPath, "manifest.bin")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.CorpusPath, "segments"), "segment.pack", SearchOption.AllDirectories));

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var searchStats = snapshot.Search(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(2, searchStats.HitsReturned);
    }

    [Fact]
    public async Task CorruptedSegmentFailsChecksumValidation()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\nbeta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var segmentPath = Directory.EnumerateFiles(Path.Combine(fixture.CorpusPath, "segments"), "segment.pack", SearchOption.AllDirectories).Single();
        CorruptContentByte(segmentPath);

        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = storage.ReadManifest();
        Assert.Throws<InvalidDataException>(() =>
            storage.ValidateSegment(manifest.Segments[0], CancellationToken.None));
    }

    [Fact]
    public async Task ExplicitValidationSurfacesCorruptionBeforeQueries()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\nbeta\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        var segmentPath = Directory.EnumerateFiles(
            Path.Combine(fixture.CorpusPath, "segments"),
            "segment.pack",
            SearchOption.AllDirectories).Single();
        CorruptContentByte(segmentPath);

        using var reopened = new SiftIndex(fixture.CorpusPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.Validate());

        Assert.True(reopened.RequiresRebuild);
        Assert.Equal(IndexValidationState.Failed, reopened.GetValidationStats().State);
        Assert.NotNull(reopened.GetValidationStats().Failure);
    }

    [Fact]
    public async Task ExplicitValidationRejectsMalformedLineIndexes()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\nbeta\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        var segmentPath = Directory.EnumerateFiles(
            Path.Combine(fixture.CorpusPath, "segments"),
            "segment.pack",
            SearchOption.AllDirectories).Single();
        RewriteChecksummedRegion(
            segmentPath,
            SegmentRegionId.LineIndexBlob,
            static (region, _) => BinaryPrimitives.WriteInt32LittleEndian(region[4..], int.MaxValue));

        using var reopened = new SiftIndex(fixture.CorpusPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.Validate());
        Assert.True(reopened.RequiresRebuild);
    }

    [Fact]
    public async Task ExplicitValidationRejectsUnorderedDocumentPaths()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        var segmentPath = Directory.EnumerateFiles(
            Path.Combine(fixture.CorpusPath, "segments"),
            "segment.pack",
            SearchOption.AllDirectories).Single();
        RewriteChecksummedRegion(
            segmentPath,
            SegmentRegionId.PathBlob,
            static (region, _) => region[4] = (byte)'z');

        using var reopened = new SiftIndex(fixture.CorpusPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.Validate());
        Assert.True(reopened.RequiresRebuild);
    }

    [Fact]
    public async Task OnlineDocumentAccessRejectsInvalidContentBoundsAndRequiresRebuild()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\nbeta\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        var segmentPath = Directory.EnumerateFiles(
            Path.Combine(fixture.CorpusPath, "segments"),
            "segment.pack",
            SearchOption.AllDirectories).Single();
        CorruptFirstDocumentContentOffset(segmentPath);

        using var reopened = new SiftIndex(fixture.CorpusPath);
        using var snapshot = reopened.OpenSnapshot();
        Assert.Throws<InvalidDataException>(() =>
            snapshot.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out _));
        Assert.True(reopened.RequiresRebuild);
    }

    [Fact]
    public async Task CollectorFailureDoesNotRequireRebuild()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha\n")]));
        using var snapshot = index.OpenSnapshot();

        Assert.Throws<InvalidDataException>(() => snapshot.Search(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            new FailingCollector()));
        Assert.False(index.RequiresRebuild);
    }

    [Fact]
    public async Task OnlineUpdateDefersFullChecksumValidation()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\nbeta\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        var segmentPath = Directory.EnumerateFiles(
            Path.Combine(fixture.CorpusPath, "segments"),
            "segment.pack",
            SearchOption.AllDirectories).Single();
        CorruptContentByte(segmentPath);

        using var restarted = new SiftIndex(fixture.CorpusPath);
        var update = await restarted.Update(
            new InMemoryDocumentSource([]),
            [new DocumentKey("src/a.txt")]);

        Assert.Equal(1, update.DeletedDocuments);
        Assert.False(restarted.RequiresRebuild);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.Validate());
        Assert.True(restarted.RequiresRebuild);
    }

    [Fact]
    public async Task CorruptedSegmentHeaderIsRejected()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var segmentPath = Directory.EnumerateFiles(Path.Combine(fixture.CorpusPath, "segments"), "segment.pack", SearchOption.AllDirectories).Single();
        using (var stream = new FileStream(segmentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = 0;
            stream.WriteByte(0x00);
        }

        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = storage.ReadManifest();
        Assert.Throws<InvalidDataException>(() => storage.OpenSegment(manifest.Segments[0].SegmentId));
    }

    [Fact]
    public async Task InvalidPostingsBoundsAreRejected()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var segmentPath = Directory.EnumerateFiles(Path.Combine(fixture.CorpusPath, "segments"), "segment.pack", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(segmentPath);
        var regionEntryOffset = 60 + (5 - 1) * 20;
        var lexiconOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(regionEntryOffset + 4, 8));
        var lexiconLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(regionEntryOffset + 12, 8));
        var checksumTableOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(52, 8));
        var firstEntryOffset = (int)lexiconOffset + SegmentFormat.LexiconHeaderSize;
        var bucket = 0;
        while (bucket < 256)
        {
            var start = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)lexiconOffset + 4 + bucket * 4, 4));
            var end = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)lexiconOffset + 4 + (bucket + 1) * 4, 4));
            if (start < end)
            {
                break;
            }

            bucket++;
        }

        Assert.True(bucket < 256);
        var suffix = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(firstEntryOffset, 2));
        var corruptedGram = (bucket << 16) | suffix;
        using (var stream = new FileStream(segmentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(firstEntryOffset + 2, 4), int.MaxValue);
            stream.Position = firstEntryOffset + 2;
            stream.Write(BitConverter.GetBytes(int.MaxValue));

            BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan((int)(checksumTableOffset + (5 - 1) * 12 + 4), 8),
                Hashing.Fnv1A64(bytes.AsSpan((int)lexiconOffset, (int)lexiconLength)));
            stream.Position = checksumTableOffset + (5 - 1) * 12 + 4;
            stream.Write(bytes, (int)(checksumTableOffset + (5 - 1) * 12 + 4), 8);
        }

        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = storage.ReadManifest();
        using var segment = storage.OpenSegment(manifest.Segments[0].SegmentId);
        Assert.Throws<InvalidDataException>(() => segment.TryGetPostings(new IndexRegionPair(SegmentRegionId.ContentTriCsLex, SegmentRegionId.ContentTriCsPost), corruptedGram, out _, out _));
        Assert.Throws<InvalidDataException>(() =>
            storage.ValidateSegment(manifest.Segments[0], CancellationToken.None));
    }

    [Fact]
    public async Task InvalidRegionBoundsAreRejected()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var segmentPath = Directory.EnumerateFiles(Path.Combine(fixture.CorpusPath, "segments"), "segment.pack", SearchOption.AllDirectories).Single();
        using (var stream = new FileStream(segmentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = 60 + 4;
            var badOffset = BitConverter.GetBytes(long.MaxValue);
            stream.Write(badOffset, 0, badOffset.Length);
        }

        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = storage.ReadManifest();
        Assert.Throws<InvalidDataException>(() => storage.OpenSegment(manifest.Segments[0].SegmentId));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 18)]
    [InlineData(false, 25)]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, 0)]
    [InlineData(true, 18)]
    [InlineData(true, 25)]
    [InlineData(true, int.MaxValue)]
    public async Task UnknownRegionIdsAreRejected(bool checksumTable, int regionId)
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        var segmentPath = Directory.EnumerateFiles(
            Path.Combine(fixture.CorpusPath, "segments"), "segment.pack", SearchOption.AllDirectories).Single();
        using (var stream = new FileStream(segmentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = checksumTable ? 52 : 44;
            var offset = new byte[sizeof(long)];
            stream.ReadExactly(offset);
            stream.Position = BinaryPrimitives.ReadInt64LittleEndian(offset);
            var id = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(id, regionId);
            stream.Write(id);
        }

        using var reopened = new SiftIndex(fixture.CorpusPath);
        using var snapshot = reopened.OpenSnapshot();
        Assert.Throws<InvalidDataException>(() => snapshot.Search(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), new HitBuffer()));
        Assert.True(reopened.RequiresRebuild);
    }

    [Fact]
    public async Task StaleTempEntriesAreRecovered()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        Directory.CreateDirectory(Path.Combine(fixture.CorpusPath, "temp", "stale-dir"));
        File.WriteAllText(Path.Combine(fixture.CorpusPath, "temp", "stale-file.tmp"), "stale");

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.CorpusPath, "temp")));
    }

    [Fact]
    public async Task StaleManifestTempIsRecovered()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        Directory.CreateDirectory(fixture.CorpusPath);
        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = new CorpusManifest(new CorpusId(Guid.NewGuid()), new StorageGeneration(7), []);
        using (var writeSession = storage.AcquireWriteSession(CancellationToken.None))
        {
            writeSession.PublishManifest(manifest, 0);
        }
        File.Move(storage.ManifestPath, storage.ManifestPath + ".tmp", overwrite: true);

        storage.EnsureLayout();

        Assert.True(File.Exists(storage.ManifestPath));
        Assert.False(File.Exists(storage.ManifestPath + ".tmp"));
        var recovered = storage.ReadManifest();
        Assert.Equal(new StorageGeneration(7), recovered.Generation);
    }

    [Fact]
    public async Task TruncatedInitialManifestTempIsDiscardedBeforeRebuild()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        Directory.CreateDirectory(fixture.CorpusPath);
        File.WriteAllBytes(Path.Combine(fixture.CorpusPath, "manifest.bin.tmp"), [0x01, 0x02, 0x03]);

        using var index = new SiftIndex(fixture.CorpusPath);
        var stats = await index.Build(fixture.CreateSource());

        Assert.Equal(1, stats.IndexedDocuments);
        Assert.False(File.Exists(Path.Combine(fixture.CorpusPath, "manifest.bin.tmp")));
        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            collector);
        Assert.Single(collector.Hits);
    }

    [Fact]
    public async Task FullBuildReclaimsPacksLeftWithoutManifest()
    {
        using var fixture = new CorpusFixture(("src/current.txt", "current\n"));
        var abandonedStorage = new CorpusStorage(fixture.CorpusPath);
        SegmentManifestEntry abandoned;
        using (var writeSession = abandonedStorage.AcquireWriteSession(CancellationToken.None))
        {
            abandoned = writeSession.PublishSegment(
                [CreateStoredDocument("src/abandoned.txt", "abandoned\n")],
                new SegmentId(0x1234UL),
                minimumFreeBytesAfterPublication: 0,
                CancellationToken.None);
        }
        var abandonedPath = Path.Combine(
            abandonedStorage.SegmentsPath,
            abandoned.SegmentId.ToString("X16"),
            "segment.pack");
        Assert.True(File.Exists(abandonedPath));
        Assert.False(abandonedStorage.HasManifest);

        using var index = new SiftIndex(fixture.CorpusPath);
        var stats = await index.Build(fixture.CreateSource());

        Assert.Equal(1, stats.IndexedDocuments);
        Assert.False(File.Exists(abandonedPath));
        using var snapshot = index.OpenSnapshot();
        var current = new HitBuffer();
        snapshot.Search(
            new SearchQuery("current", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            current);
        Assert.Single(current.Hits);
    }

    [Fact]
    public async Task IncompleteSegmentDirectoriesAreRecovered()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        Directory.CreateDirectory(Path.Combine(fixture.CorpusPath, "segments", "DEADBEEFDEADBEEF"));
        Directory.CreateDirectory(Path.Combine(fixture.CorpusPath, "segments", "BADBADBADBADBADA"));
        File.WriteAllText(Path.Combine(fixture.CorpusPath, "segments", "BADBADBADBADBADA", "segment.pack"), "not-a-segment");

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        Assert.DoesNotContain(Path.Combine(fixture.CorpusPath, "segments", "DEADBEEFDEADBEEF"), Directory.EnumerateDirectories(Path.Combine(fixture.CorpusPath, "segments")));
        Assert.DoesNotContain(Path.Combine(fixture.CorpusPath, "segments", "BADBADBADBADBADA"), Directory.EnumerateDirectories(Path.Combine(fixture.CorpusPath, "segments")));
    }

    [Fact]
    public async Task RecoveryPreservesCorruptSegmentReferencedByCurrentManifest()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        var segmentPath = Directory.EnumerateFiles(
            Path.Combine(fixture.CorpusPath, "segments"),
            "segment.pack",
            SearchOption.AllDirectories).Single();
        using (var stream = new FileStream(segmentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = 0;
            stream.WriteByte(0);
        }

        var storage = new CorpusStorage(fixture.CorpusPath);
        Assert.Throws<InvalidDataException>(() => storage.EnsureLayout());
        Assert.True(File.Exists(segmentPath));
    }

    [Fact]
    public async Task InMemoryRecoveryPreservesLiveSnapshots()
    {
        var storage = new BlockingValidationStorage(new InMemorySiftStorage());
        using var index = new SiftIndex(storage, new SiftOptions());
        await index.Build(new InMemoryDocumentSource([("src/old.txt", "old")]));
        using var oldSnapshot = index.OpenSnapshot();
        var validation = index.Validate(CancellationToken.None);
        Assert.True(storage.ValidationStarted.Wait(TimeSpan.FromSeconds(5)));
        storage.ReleaseValidation.Set();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await validation);
        Assert.True(index.RequiresRebuild);

        await index.Build(new InMemoryDocumentSource([("src/new.txt", "new")]));

        Assert.True(oldSnapshot.TryGetDocumentBytes(
            new DocumentKey("src/old.txt"),
            out var oldContent));
        Assert.Equal("old"u8.ToArray(), oldContent.ToArray());
        using var newSnapshot = index.OpenSnapshot();
        Assert.True(newSnapshot.TryGetDocumentBytes(new DocumentKey("src/new.txt"), out _));
    }

    [Fact]
    public async Task FileRecoveryRejectsLiveSnapshotsBeforeDeletingPacks()
    {
        using var fixture = new CorpusFixture(("src/old.txt", "old"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var oldSnapshot = index.OpenSnapshot();
        try
        {
            var segmentPath = Directory.EnumerateFiles(
                Path.Combine(fixture.CorpusPath, "segments"),
                "segment.pack",
                SearchOption.AllDirectories).Single();
            var segmentBytes = File.ReadAllBytes(segmentPath);
            CorruptContentByte(segmentPath);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await index.Validate(CancellationToken.None));
            File.WriteAllBytes(segmentPath, segmentBytes);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await index.Build(new InMemoryDocumentSource([("src/new.txt", "new")])));

            Assert.True(oldSnapshot.TryGetDocumentBytes(
                new DocumentKey("src/old.txt"),
                out var oldContent));
            Assert.Equal("old"u8.ToArray(), oldContent.ToArray());
        }
        finally
        {
            oldSnapshot.Dispose();
        }

        await index.Build(new InMemoryDocumentSource([("src/new.txt", "new")]));
        using var newSnapshot = index.OpenSnapshot();
        Assert.True(newSnapshot.TryGetDocumentBytes(new DocumentKey("src/new.txt"), out _));
    }

    [Fact]
    public void TruncatedSegmentIsRejectedAsInvalidData()
    {
        using var fixture = new CorpusFixture();
        var path = Path.Combine(fixture.CorpusPath, "truncated.pack");
        Directory.CreateDirectory(fixture.CorpusPath);
        File.WriteAllBytes(path, [0x01, 0x02, 0x03]);

        Assert.Throws<InvalidDataException>(() =>
            new MappedSegment(path, validateChecksums: true, CancellationToken.None));
    }

    [Fact]
    public async Task ManifestDeadOrdinalsRoundTripThroughCompressedEncoding()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = new CorpusManifest(
            CorpusId: new CorpusId(Guid.NewGuid()),
            ContentRevision: new ContentRevision(2),
            Generation: new StorageGeneration(3),
            CommitMetadata: "checkpoint"u8.ToArray(),
            Segments: [
                new SegmentManifestEntry(new SegmentId(42), 6000, 100, DeadOrdinalSet.FromOwned([1, 2, 3, 1000, 5000]), DeadContentBytes: 5)
            ]);

        using (var writeSession = storage.AcquireWriteSession(CancellationToken.None))
        {
            writeSession.PublishManifest(manifest, 0);
        }
        var roundTripped = storage.ReadManifest();

        Assert.Equal(manifest.Generation, roundTripped.Generation);
        Assert.Equal(manifest.ContentRevision, roundTripped.ContentRevision);
        Assert.Equal(manifest.CommitMetadata, roundTripped.CommitMetadata);
        Assert.Equal(manifest.Segments[0].DeadOrdinals, roundTripped.Segments[0].DeadOrdinals);
        Assert.True(new FileInfo(storage.ManifestPath).Length <
            SegmentFormat.ManifestHeaderSize +
            SegmentFormat.ManifestEntryHeaderSize +
            manifest.CommitMetadata.Length +
            sizeof(ulong) +
            manifest.Segments[0].DeadOrdinals.Length * sizeof(uint));
    }

    [Fact]
    public async Task MappedSegmentTracksOpenMappedBytes()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = storage.ReadManifest();
        var baselineBytes = SiftProcessMetrics.MappedSegmentBytes;
        var baselineOpen = SiftProcessMetrics.OpenSegments;

        using (var segment = storage.OpenSegment(manifest.Segments[0].SegmentId))
        {
            Assert.True(SiftProcessMetrics.MappedSegmentBytes > baselineBytes);
            Assert.True(SiftProcessMetrics.OpenSegments >= baselineOpen + 1);
        }

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while ((SiftProcessMetrics.MappedSegmentBytes > baselineBytes || SiftProcessMetrics.OpenSegments > baselineOpen) &&
               Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.True(SiftProcessMetrics.MappedSegmentBytes <= baselineBytes);
        Assert.True(SiftProcessMetrics.OpenSegments <= baselineOpen);
    }

    [Fact]
    public async Task MappedSegmentDisposeIsIdempotent()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var storage = new CorpusStorage(fixture.CorpusPath);
        var segmentId = storage.ReadManifest().Segments.Single().SegmentId;
        var baselineOpen = SiftProcessMetrics.OpenSegments;
        var segment = storage.OpenSegment(segmentId);

        segment.Dispose();
        segment.Dispose();

        Assert.Equal(baselineOpen, SiftProcessMetrics.OpenSegments);
    }

    [Fact]
    public async Task OnlineOpensDeferFullChecksumsToExplicitValidation()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        using var reopened = new SiftIndex(fixture.CorpusPath);
        var storage = TestHelpers.GetStorage(reopened);
        Assert.Equal(0, storage.ChecksumValidationCount);

        using (reopened.OpenSnapshot())
        {
        }

        Assert.Equal(0, storage.ChecksumValidationCount);
        using (var snapshot = reopened.OpenSnapshot())
        {
            snapshot.Explain(
                new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
                new PathFilter());
        }

        Assert.Equal(0, storage.ChecksumValidationCount);
        await reopened.Validate();
        Assert.Equal(1, storage.ChecksumValidationCount);
        using (var snapshot = reopened.OpenSnapshot())
        {
            snapshot.Explain(
                new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
                new PathFilter());
        }

        Assert.Equal(1, storage.ChecksumValidationCount);
    }

    [Fact]
    public async Task ExplicitValidationChecksEverySegmentAndReportsReadiness()
    {
        using var fixture = new CorpusFixture();
        var options = new SiftOptions { TargetSegmentContentBytes = 5 };
        using (var index = new SiftIndex(fixture.CorpusPath, options))
        {
            await index.Build(new InMemoryDocumentSource([
                ("src/a.txt", "alpha"),
                ("src/b.txt", "bravo"),
                ("src/c.txt", "cello")
            ]));
        }

        using var reopened = new SiftIndex(fixture.CorpusPath, options);
        var storage = TestHelpers.GetStorage(reopened);
        Assert.Equal(IndexValidationState.NotStarted, reopened.GetValidationStats().State);
        Assert.Equal(0, storage.ChecksumValidationCount);

        var validation = await reopened.Validate();

        Assert.True(validation.IsReady);
        Assert.Equal(3, validation.TotalSegments);
        Assert.Equal(validation.TotalSegments, validation.ValidatedSegments);
        Assert.Equal(validation.TotalBytes, validation.ValidatedBytes);
        Assert.Equal(3, storage.ChecksumValidationCount);
        Assert.Equal(validation, reopened.GetValidationStats());
    }

    [Fact]
    public async Task RepeatedExplicitValidationRechecksSegmentChecksums()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        using var reopened = new SiftIndex(fixture.CorpusPath);
        Assert.True((await reopened.Validate()).IsReady);
        var segmentPath = Directory.EnumerateFiles(
            Path.Combine(fixture.CorpusPath, "segments"),
            "segment.pack",
            SearchOption.AllDirectories).Single();
        CorruptContentByte(segmentPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.Validate());
        Assert.True(reopened.RequiresRebuild);
    }

    [Fact]
    public async Task ExplicitValidationDoesNotAcquireTheWriterLock()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        using var reopened = new SiftIndex(fixture.CorpusPath);
        var storage = TestHelpers.GetStorage(reopened);
        using var writeSession = storage.AcquireWriteSession(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var validation = await reopened.Validate(cancellation.Token);

        Assert.True(validation.IsReady);
    }

    [Fact]
    public async Task MutationInvalidatesReportedValidationReadiness()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        Assert.True((await index.Validate()).IsReady);

        await index.Update(new InMemoryDocumentSource([("src/a.txt", "omega")]), []);

        Assert.Equal(IndexValidationState.NotStarted, index.GetValidationStats().State);
    }

    [Fact]
    public async Task FailedValidationMarksRebuildWhenConcurrentUpdateRetainsSegment()
    {
        var storage = new BlockingValidationStorage(new InMemorySiftStorage());
        using var index = new SiftIndex(storage, new SiftOptions());
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));

        var validation = index.Validate();
        try
        {
            Assert.True(storage.ValidationStarted.Wait(TimeSpan.FromSeconds(5)));
            await index.Update(new InMemoryDocumentSource([("src/b.txt", "beta")]), []);
        }
        finally
        {
            storage.ReleaseValidation.Set();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => validation);
        Assert.True(index.RequiresRebuild);
        Assert.Equal(IndexValidationState.Failed, index.GetValidationStats().State);
    }

    [Fact]
    public async Task FailedValidationDoesNotPoisonConcurrentReplacementBuild()
    {
        var storage = new BlockingValidationStorage(new InMemorySiftStorage());
        using var index = new SiftIndex(storage, new SiftOptions());
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));

        var validation = index.Validate();
        try
        {
            Assert.True(storage.ValidationStarted.Wait(TimeSpan.FromSeconds(5)));
            await index.Build(new InMemoryDocumentSource([("src/b.txt", "beta")]));
        }
        finally
        {
            storage.ReleaseValidation.Set();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => validation);
        Assert.False(index.RequiresRebuild);
        Assert.Equal(IndexValidationState.NotStarted, index.GetValidationStats().State);
    }

    [Fact]
    public async Task StaleValidationReconciliationObservesCancellation()
    {
        var storage = new BlockingValidationStorage(new InMemorySiftStorage());
        using var index = new SiftIndex(storage, new SiftOptions());
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        using var cancellation = new CancellationTokenSource();
        var validation = index.Validate(cancellation.Token);
        Assert.True(storage.ValidationStarted.Wait(TimeSpan.FromSeconds(5)));
        await index.Update(new InMemoryDocumentSource([("src/b.txt", "beta")]), []);

        using (storage.BlockWriterLock())
        {
            storage.ReleaseValidation.Set();
            Assert.True(storage.ValidationFailed.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await validation.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.True(index.RequiresRebuild);
        Assert.Equal(IndexValidationState.Failed, index.GetValidationStats().State);
    }

    [Fact]
    public async Task LegacyManifestDeadBytesAreCachedAndMigratedOnMutation()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
            await index.Update(
                new InMemoryDocumentSource([]),
                [new DocumentKey("src/a.txt")]);
        }

        var manifestPath = Path.Combine(fixture.CorpusPath, "manifest.bin");
        RewriteManifestAsLegacyV2(manifestPath);
        using var reopened = new SiftIndex(fixture.CorpusPath);
        var storage = TestHelpers.GetStorage(reopened);

        var first = reopened.GetMaintenanceStats();
        var segmentOpensAfterFirstStats = storage.SegmentOpenCount;
        var second = reopened.GetMaintenanceStats();

        Assert.Equal("alpha\n".Length, first.EstimatedReclaimableContentBytes);
        Assert.Equivalent(first, second, strict: true);
        Assert.Equal(segmentOpensAfterFirstStats, storage.SegmentOpenCount);

        await reopened.Update(new InMemoryDocumentSource([]), []);

        var manifestBytes = File.ReadAllBytes(manifestPath);
        Assert.Equal(
            SegmentFormat.ManifestFormatVersion,
            BinaryPrimitives.ReadInt32LittleEndian(manifestBytes.AsSpan(8)));
        Assert.Equal(
            "alpha\n".Length,
            storage.ReadManifest().Segments.Single().DeadContentBytes);
    }

    [Fact]
    public async Task CorruptedManifestChecksumIsRejected()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using (var stream = new FileStream(Path.Combine(fixture.CorpusPath, "manifest.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = stream.Length - 1;
            var current = stream.ReadByte();
            stream.Position--;
            stream.WriteByte(unchecked((byte)(current ^ 0x5A)));
        }

        var storage = new CorpusStorage(fixture.CorpusPath);
        Assert.Throws<InvalidDataException>(() => storage.ReadManifest());
    }

    [Fact]
    public void ManifestRejectsDuplicateSegmentIds()
    {
        using var fixture = new CorpusFixture();
        var entry = new SegmentManifestEntry(
            new SegmentId(1),
            DocumentCount: 0,
            ContentBytes: 0,
            DeadOrdinalSet.Empty,
            DeadContentBytes: 0);
        var manifest = new CorpusManifest(
            CorpusId.New(),
            new StorageGeneration(1),
            [entry, entry]);
        var path = Path.Combine(fixture.CorpusPath, "duplicate-manifest.bin");
        Directory.CreateDirectory(fixture.CorpusPath);
        File.WriteAllBytes(path, CorpusManifestCodec.Encode(manifest));

        Assert.Throws<InvalidDataException>(() => CorpusManifestCodec.Read(path));
    }

    [Fact]
    public async Task ValidationRejectsManifestMetadataThatDoesNotMatchItsPack()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        RewriteChecksummedManifest(
            Path.Combine(fixture.CorpusPath, "manifest.bin"),
            bytes => BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(SegmentFormat.ManifestHeaderSize + sizeof(ulong)),
                2));

        using var reopened = new SiftIndex(fixture.CorpusPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.Validate());

        Assert.True(reopened.RequiresRebuild);
    }

    [Fact]
    public async Task ExplicitValidationRechecksACachedManifestWithAnUnchangedStamp()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var manifestPath = Path.Combine(fixture.CorpusPath, "manifest.bin");
        var lastWriteTime = File.GetLastWriteTimeUtc(manifestPath);

        using (var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = stream.Length - 1;
            var current = stream.ReadByte();
            stream.Position--;
            stream.WriteByte(unchecked((byte)(current ^ 0x5A)));
        }

        File.SetLastWriteTimeUtc(manifestPath, lastWriteTime);

        await Assert.ThrowsAsync<InvalidDataException>(() => index.Validate());
        Assert.True(index.RequiresRebuild);
    }

    [Fact]
    public async Task ManifestSegmentCountMustFitTheEncodedTable()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var manifestPath = Path.Combine(fixture.CorpusPath, "manifest.bin");
        RewriteChecksummedManifest(
            manifestPath,
            static bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), int.MaxValue));

        var storage = new CorpusStorage(fixture.CorpusPath);
        Assert.Throws<InvalidDataException>(() => storage.ReadManifest());
    }

    [Fact]
    public async Task ManifestDeadOrdinalCountMustFitItsPayload()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var manifestPath = Path.Combine(fixture.CorpusPath, "manifest.bin");
        RewriteChecksummedManifest(
            manifestPath,
            static bytes =>
            {
                var entryOffset = SegmentFormat.ManifestHeaderSize;
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entryOffset + 8), int.MaxValue);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entryOffset + 20), int.MaxValue);
            });

        var storage = new CorpusStorage(fixture.CorpusPath);
        Assert.Throws<InvalidDataException>(() => storage.ReadManifest());
    }

    [Fact]
    public async Task BuildPartitionsIntoMultipleSegmentsWhenThresholdExceeded()
    {
        var documents = Enumerable.Range(0, 50_001)
            .Select(static i => ($"src/{i:D6}.txt", "x\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        var stats = await index.Build(fixture.CreateSource());

        Assert.True(stats.SegmentCount > 1);
        var storage = new CorpusStorage(fixture.CorpusPath);
        Assert.True(storage.ReadManifest().Segments.Count > 1);
    }

    [Fact]
    public async Task PathTrigramPostingsArePersisted()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"),
            ("tests/c.txt", "gamma\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = storage.ReadManifest();
        using var segment = storage.OpenSegment(manifest.Segments[0].SegmentId);

        Assert.True(segment.TryGetPostings(new IndexRegionPair(SegmentRegionId.PathTriCsLex, SegmentRegionId.PathTriCsPost), TestHelpers.EncodeGram("src"), out var postings, out var docFreq));
        Assert.Equal(2, docFreq);
        Assert.Equal(new uint[] { 0, 1 }, Decode(postings));
    }



    [Fact]
    public async Task StatsRegionStoresNonAsciiDocumentDensity()
    {
        using var fixture = new CorpusFixture(
            ("src/ascii.txt", "alpha"),
            ("src/unicode.txt", "café"),
            ("src/empty.txt", string.Empty));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = storage.ReadManifest();
        using var segment = storage.OpenSegment(manifest.Segments[0].SegmentId);

        var stats = segment.GetNonAsciiDocumentStats();
        Assert.Equal(1, stats.DocumentCount);
        Assert.Equal("café"u8.Length, stats.ContentBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public async Task ValidationRejectsInvalidRetainedStatistics(int offset)
    {
        using var fixture = new CorpusFixture(("src/a.txt", "café"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        var segmentPath = Directory.EnumerateFiles(
            Path.Combine(fixture.CorpusPath, "segments"), "segment.pack", SearchOption.AllDirectories).Single();
        RewriteChecksummedRegion(segmentPath, SegmentRegionId.Stats, (region, _) =>
        {
            if (offset == 12)
                BinaryPrimitives.WriteInt64LittleEndian(region[offset..], -1);
            else
                BinaryPrimitives.WriteInt32LittleEndian(region[offset..], -1);
        });

        using var reopened = new SiftIndex(fixture.CorpusPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.Validate());
        Assert.True(reopened.RequiresRebuild);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task UnsupportedSegmentFormatRequiresExplicitRebuild(int version)
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha"));
        using (var original = new SiftIndex(fixture.CorpusPath))
        {
            await original.Build(fixture.CreateSource());
        }

        var segmentPath = Directory.EnumerateFiles(
            Path.Combine(fixture.CorpusPath, "segments"), "segment.pack", SearchOption.AllDirectories).Single();
        // Unknown versions and a version-3 header over version-4 stats must
        // fail closed rather than guessing which format to interpret.
        using (var stream = new FileStream(segmentPath, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 8;
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, version);
            stream.Write(bytes);
        }

        using var reopened = new SiftIndex(fixture.CorpusPath);
        using (var obsoleteSnapshot = reopened.OpenSnapshot())
        {
            Assert.Throws<InvalidDataException>(() => obsoleteSnapshot.Search(
                new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), new HitBuffer()));
        }
        Assert.True(reopened.RequiresRebuild);
        Assert.Throws<InvalidDataException>(() => reopened.OpenSnapshot());
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.Update(
            new InMemoryDocumentSource([("src/b.txt", "beta")]), []));
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.Compact(new CompactOptions()));

        await reopened.Build(fixture.CreateSource());
        Assert.False(reopened.RequiresRebuild);
        await reopened.Validate();
        using var snapshot = reopened.OpenSnapshot();
        var stats = snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(), new HitBuffer());
        Assert.Equal(1, stats.HitsReturned);
    }


    [Fact]
    public async Task LargePostingsCarrySkipCheckpoints()
    {
        var documents = Enumerable.Range(0, 5000)
            .Select(static i => ($"src/{i:D5}.txt", "alpha needle gamma\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = storage.ReadManifest();
        using var segment = storage.OpenSegment(manifest.Segments[0].SegmentId);

        Assert.True(segment.TryGetPostings(new IndexRegionPair(SegmentRegionId.ContentTriCsLex, SegmentRegionId.ContentTriCsPost), TestHelpers.EncodeGram("nee"), out var postings, out var docFreq));
        Assert.Equal(5000, docFreq);
        var view = PostingsCodec.Parse(postings);
        Assert.True(view.IsCheckpointed);
        Assert.True(view.Checkpoints.Length >= 8);
    }

    [Fact]
    public async Task CrashBeforeSegmentPublicationLeavesOnlyRecoverableTempState()
    {
        using var fixture = new CorpusFixture();
        var storage = new CorpusStorage(fixture.CorpusPath)
        {
            FaultInjector = new PublicationFaultInjector
            {
                ThrowOnStage = PublicationStage.AfterSegmentFlushBeforePublish
            }
        };

        var documents = new[] { CreateStoredDocument("src/a.txt", "alpha\n") };
        using (var writeSession = storage.AcquireWriteSession(CancellationToken.None))
        {
            Assert.Throws<IOException>(() => writeSession.PublishSegment(
                documents,
                new SegmentId(0x1234UL),
                minimumFreeBytesAfterPublication: 0,
                CancellationToken.None));
        }

        Assert.Empty(Directory.EnumerateDirectories(storage.SegmentsPath));
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(storage.TempPath));

        storage.EnsureLayout();

        Assert.Empty(Directory.EnumerateDirectories(storage.SegmentsPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TempPath));
    }

    [Fact]
    public void SegmentPublicationRequiresDurableReclamationIntent()
    {
        using var fixture = new CorpusFixture();
        var storage = new CorpusStorage(fixture.CorpusPath);
        storage.EnsureLayout();
        storage.FaultInjector = new PublicationFaultInjector
        {
            ThrowOnStage = PublicationStage.BeforeReclamationIntentFlush
        };

        using (var writeSession = storage.AcquireWriteSession(CancellationToken.None))
        {
            Assert.Throws<IOException>(() => writeSession.PublishSegment(
                [CreateStoredDocument("src/a.txt", "alpha\n")],
                new SegmentId(0x1234UL),
                minimumFreeBytesAfterPublication: 0,
                CancellationToken.None));
        }

        Assert.Empty(Directory.EnumerateDirectories(storage.SegmentsPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TempPath));
        Assert.Equal(
            (byte)0,
            File.ReadAllBytes(Path.Combine(fixture.CorpusPath, "lock", "reclamation.state")).Single());
    }

    [Fact]
    public async Task ManifestPublicationRequiresDurableReclamationIntent()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
        }

        var storage = new CorpusStorage(fixture.CorpusPath);
        var original = storage.ReadManifest();
        Assert.Equal(
            (byte)0,
            File.ReadAllBytes(Path.Combine(fixture.CorpusPath, "lock", "reclamation.state")).Single());
        storage.FaultInjector = new PublicationFaultInjector
        {
            ThrowOnStage = PublicationStage.BeforeReclamationIntentFlush
        };

        var next = new CorpusManifest(original.CorpusId, original.Generation.Next(), original.Segments);
        using (var writeSession = storage.AcquireWriteSession(CancellationToken.None))
        {
            Assert.Throws<IOException>(() => writeSession.PublishManifest(next, 0));
        }

        Assert.Equal(original.Generation, storage.ReadManifest().Generation);
        Assert.False(File.Exists(storage.ManifestPath + ".tmp"));
    }

    [Fact]
    public async Task CrashAfterManifestFlushWithExistingManifestPreservesPreviousManifest()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var storage = new CorpusStorage(fixture.CorpusPath);
        var original = storage.ReadManifest();
        storage.FaultInjector = new PublicationFaultInjector
        {
            ThrowOnStage = PublicationStage.AfterManifestFlushBeforeSwap
        };

        var next = new CorpusManifest(original.CorpusId, original.Generation.Next(), original.Segments);
        using (var writeSession = storage.AcquireWriteSession(CancellationToken.None))
        {
            Assert.Throws<IOException>(() => writeSession.PublishManifest(next, 0));
        }

        Assert.True(File.Exists(storage.ManifestPath + ".tmp"));
        storage.EnsureLayout();

        var recovered = storage.ReadManifest();
        Assert.Equal(original.Generation, recovered.Generation);
        Assert.False(File.Exists(storage.ManifestPath + ".tmp"));
    }

    [Fact]
    public async Task CrashAfterManifestFlushWithoutExistingManifestRecoversTempManifest()
    {
        using var fixture = new CorpusFixture();
        var storage = new CorpusStorage(fixture.CorpusPath)
        {
            FaultInjector = new PublicationFaultInjector
            {
                ThrowOnStage = PublicationStage.AfterManifestFlushBeforeSwap
            }
        };

        var manifest = new CorpusManifest(new CorpusId(Guid.NewGuid()), new StorageGeneration(1), []);
        using (var writeSession = storage.AcquireWriteSession(CancellationToken.None))
        {
            Assert.Throws<IOException>(() => writeSession.PublishManifest(manifest, 0));
        }

        Assert.False(File.Exists(storage.ManifestPath));
        Assert.True(File.Exists(storage.ManifestPath + ".tmp"));

        storage.EnsureLayout();

        var recovered = storage.ReadManifest();
        Assert.Equal(manifest.Generation, recovered.Generation);
        Assert.False(File.Exists(storage.ManifestPath + ".tmp"));
    }

    [Fact]
    public async Task CrashDuringCompactionStyleManifestSwapKeepsOldSnapshotVisible()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var storage = new CorpusStorage(fixture.CorpusPath);
        var original = storage.ReadManifest();
        SegmentManifestEntry compactedEntry;
        using (var writeSession = storage.AcquireWriteSession(CancellationToken.None))
        {
            compactedEntry = writeSession.PublishSegment(
                [CreateStoredDocument("src/a.txt", "omega\n")],
                new SegmentId(0x7777UL),
                minimumFreeBytesAfterPublication: 0,
                CancellationToken.None);
        }

        storage.FaultInjector = new PublicationFaultInjector
        {
            ThrowOnStage = PublicationStage.AfterManifestFlushBeforeSwap
        };

        var failedManifest = new CorpusManifest(original.CorpusId, original.Generation.Next(), [compactedEntry]);
        using (var writeSession = storage.AcquireWriteSession(CancellationToken.None))
        {
            Assert.Throws<IOException>(() => writeSession.PublishManifest(failedManifest, 0));
        }

        storage.EnsureLayout();

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);
        Assert.Single(collector.Hits);

        var omega = new HitBuffer();
        snapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), omega);
        Assert.Empty(omega.Hits);
    }

    private static uint[] Decode(ReadOnlySpan<byte> postings)
    {
        return PostingsCodec.Decode(postings);
    }

    private static void CorruptContentByte(string segmentPath)
    {
        var segmentBytes = File.ReadAllBytes(segmentPath);
        var contentRegionEntryOffset = SegmentFormat.HeaderSize +
            ((int)SegmentRegionId.ContentBlob - 1) * SegmentFormat.RegionDirectoryEntrySize;
        var contentOffset = BinaryPrimitives.ReadInt64LittleEndian(
            segmentBytes.AsSpan(contentRegionEntryOffset + 4, sizeof(long)));
        using var stream = new FileStream(
            segmentPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        stream.Position = contentOffset;
        var current = stream.ReadByte();
        stream.Position--;
        stream.WriteByte(unchecked((byte)(current ^ 0x5A)));
    }

    private static void CorruptFirstDocumentContentOffset(string segmentPath)
    {
        var bytes = File.ReadAllBytes(segmentPath);
        var regionDirectoryOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(44, sizeof(long)));
        var documentTableOffset = BinaryPrimitives.ReadInt64LittleEndian(
            bytes.AsSpan(checked((int)regionDirectoryOffset) + sizeof(int), sizeof(long)));
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(checked((int)documentTableOffset) + 12, sizeof(ulong)),
            ulong.MaxValue);
        File.WriteAllBytes(segmentPath, bytes);
    }

    private static void RewriteChecksummedManifest(string manifestPath, Action<byte[]> rewrite)
    {
        var bytes = File.ReadAllBytes(manifestPath);
        rewrite(bytes);
        var checksumOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(56));
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(checksumOffset),
            Hashing.Fnv1A64(bytes.AsSpan(0, checksumOffset)));
        File.WriteAllBytes(manifestPath, bytes);
    }

    private static void RewriteChecksummedRegion(
        string segmentPath,
        SegmentRegionId regionId,
        SpanAction<byte, object?> rewrite)
    {
        var bytes = File.ReadAllBytes(segmentPath);
        var directoryEntryOffset = SegmentFormat.HeaderSize +
            ((int)regionId - 1) * SegmentFormat.RegionDirectoryEntrySize;
        var regionOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(
            bytes.AsSpan(directoryEntryOffset + 4, sizeof(long))));
        var regionLength = checked((int)BinaryPrimitives.ReadInt64LittleEndian(
            bytes.AsSpan(directoryEntryOffset + 12, sizeof(long))));
        rewrite(bytes.AsSpan(regionOffset, regionLength), null);

        var checksumTableOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(
            bytes.AsSpan(52, sizeof(long))));
        var checksumOffset = checksumTableOffset +
            ((int)regionId - 1) * SegmentFormat.ChecksumEntrySize + sizeof(int);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(checksumOffset, sizeof(ulong)),
            Hashing.Fnv1A64(bytes.AsSpan(regionOffset, regionLength)));
        File.WriteAllBytes(segmentPath, bytes);
    }

    private sealed class FailingCollector : IHitCollector
    {
        public bool OnHit(in RawHit hit)
        {
            throw new InvalidDataException("Caller failure.");
        }
    }

    private static void RewriteManifestAsLegacyV2(string manifestPath)
    {
        var current = File.ReadAllBytes(manifestPath);
        var count = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(36));
        var segmentTableOffset = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(40));
        var currentSegmentTableLength = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(44));
        var legacySegmentTableLength = currentSegmentTableLength -
            count * (SegmentFormat.ManifestEntryHeaderSize - SegmentFormat.LegacyManifestEntryHeaderSize);
        var legacySegmentTableOffset = SegmentFormat.LegacyManifestHeaderSize;
        var checksumOffset = legacySegmentTableOffset + legacySegmentTableLength;
        var legacy = new byte[checksumOffset + sizeof(ulong)];
        current.AsSpan(0, SegmentFormat.LegacyManifestHeaderSize).CopyTo(legacy);
        BinaryPrimitives.WriteInt32LittleEndian(
            legacy.AsSpan(8),
            SegmentFormat.LegacyManifestFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(
            legacy.AsSpan(40),
            legacySegmentTableOffset);
        BinaryPrimitives.WriteInt32LittleEndian(
            legacy.AsSpan(44),
            legacySegmentTableLength);
        BinaryPrimitives.WriteInt32LittleEndian(
            legacy.AsSpan(48),
            checksumOffset);
        BinaryPrimitives.WriteInt32LittleEndian(
            legacy.AsSpan(52),
            sizeof(ulong));

        var currentOffset = segmentTableOffset;
        var legacyOffset = legacySegmentTableOffset;
        for (var i = 0; i < count; i++)
        {
            var deadOrdinalBytes = BinaryPrimitives.ReadInt32LittleEndian(
                current.AsSpan(currentOffset + 24));
            current.AsSpan(currentOffset, SegmentFormat.LegacyManifestEntryHeaderSize)
                .CopyTo(legacy.AsSpan(legacyOffset));
            current.AsSpan(
                    currentOffset + SegmentFormat.ManifestEntryHeaderSize,
                    deadOrdinalBytes)
                .CopyTo(legacy.AsSpan(
                    legacyOffset + SegmentFormat.LegacyManifestEntryHeaderSize));
            currentOffset += SegmentFormat.ManifestEntryHeaderSize + deadOrdinalBytes;
            legacyOffset += SegmentFormat.LegacyManifestEntryHeaderSize + deadOrdinalBytes;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(
            legacy.AsSpan(checksumOffset),
            Hashing.Fnv1A64(legacy.AsSpan(0, checksumOffset)));
        File.WriteAllBytes(manifestPath, legacy);
    }

    private static StoredDocument CreateStoredDocument(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var lineCount = bytes.Count(static value => value == (byte)'\n') + 1;
        return new StoredDocument
        {
            Path = path,
            PathUtf8 = Encoding.UTF8.GetBytes(path),
            Content = bytes,
            LineCount = lineCount,
            EncodedLineIndex = LineMap.EncodeLineIndex(bytes, lineCount),
            Flags = 0,
            MTimeUtcTicks = 0,
            ContentHash64 = Hashing.Fnv1A64(bytes)
        };
    }

    private sealed class BlockingValidationStorage(ISiftStorage inner) : ISiftStorage
    {
        private readonly ManualResetEventSlim _writerLockAllowed = new(initialState: true);
        public ManualResetEventSlim ValidationStarted { get; } = new();
        public ManualResetEventSlim ValidationFailed { get; } = new();
        public ManualResetEventSlim ReleaseValidation { get; } = new();
        public bool HasManifest => inner.HasManifest;
        public bool IsReclamationPending => inner.IsReclamationPending;

        public IStorageWriteSession AcquireWriteSession(CancellationToken cancellationToken)
        {
            _writerLockAllowed.Wait(cancellationToken);
            return inner.AcquireWriteSession(cancellationToken);
        }

        public IDisposable BlockWriterLock()
        {
            _writerLockAllowed.Reset();
            return new WriterLockBlock(_writerLockAllowed);
        }

        public CorpusManifest ReadManifest() => inner.ReadManifest();
        public CorpusManifest RefreshManifest() => inner.RefreshManifest();
        public MappedSegment OpenSegment(SegmentId segmentId) => inner.OpenSegment(segmentId);
        public MappedSegment OpenSegment(SegmentId segmentId, CancellationToken cancellationToken) =>
            inner.OpenSegment(segmentId, cancellationToken);

        public void ValidateSegment(SegmentManifestEntry entry, CancellationToken cancellationToken)
        {
            ValidationStarted.Set();
            ReleaseValidation.Wait(cancellationToken);
            ValidationFailed.Set();
            throw new InvalidDataException("Simulated retained segment corruption.");
        }

        public long GetSegmentBytes(SegmentId segmentId) => inner.GetSegmentBytes(segmentId);
        public long? GetAvailableBytes() => inner.GetAvailableBytes();
        public void EnsureSnapshotMetadata() => inner.EnsureSnapshotMetadata();
        public StorageReadSnapshot OpenReadSnapshot() => inner.OpenReadSnapshot();
        public StorageReadSnapshot OpenReadSnapshotForValidation() =>
            inner.OpenReadSnapshotForValidation();
        public StorageStats GetStorageStats() => inner.GetStorageStats();
        public void ReclaimUnreferencedSegments(CancellationToken cancellationToken) =>
            inner.ReclaimUnreferencedSegments(cancellationToken);

        private sealed class WriterLockBlock(ManualResetEventSlim writerLockAllowed) : IDisposable
        {
            public void Dispose()
            {
                writerLockAllowed.Set();
            }
        }
    }

    private sealed class SnapshotOnlyStorage(
        CorpusManifest manifest,
        IDisposable lease) : ISiftStorage
    {
        public bool HasManifest => true;
        public bool IsReclamationPending => false;
        public IStorageWriteSession AcquireWriteSession(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public CorpusManifest ReadManifest() => manifest;
        public CorpusManifest RefreshManifest() => manifest;
        public MappedSegment OpenSegment(SegmentId segmentId) => throw new NotSupportedException();
        public MappedSegment OpenSegment(SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public void ValidateSegment(SegmentManifestEntry entry, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public long GetSegmentBytes(SegmentId segmentId) => throw new NotSupportedException();
        public long? GetAvailableBytes() => throw new NotSupportedException();
        public void EnsureSnapshotMetadata() => throw new NotSupportedException();
        public StorageReadSnapshot OpenReadSnapshot() => new(manifest, lease);
        public StorageReadSnapshot OpenReadSnapshotForValidation() => throw new NotSupportedException();
        public StorageStats GetStorageStats() => throw new NotSupportedException();
        public void ReclaimUnreferencedSegments(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class BlockingPublicationStorage(ISiftStorage inner) : ISiftStorage
    {
        public ManualResetEventSlim SegmentPublished { get; } = new();
        public ManualResetEventSlim AllowManifestPublication { get; } = new();
        public bool HasManifest => inner.HasManifest;
        public bool IsReclamationPending => inner.IsReclamationPending;

        public IStorageWriteSession AcquireWriteSession(CancellationToken cancellationToken)
        {
            return new BlockingWriteSession(
                inner.AcquireWriteSession(cancellationToken),
                SegmentPublished,
                AllowManifestPublication);
        }

        public CorpusManifest ReadManifest() => inner.ReadManifest();
        public CorpusManifest RefreshManifest() => inner.RefreshManifest();
        public MappedSegment OpenSegment(SegmentId segmentId) => inner.OpenSegment(segmentId);
        public MappedSegment OpenSegment(SegmentId segmentId, CancellationToken cancellationToken) =>
            inner.OpenSegment(segmentId, cancellationToken);
        public void ValidateSegment(SegmentManifestEntry entry, CancellationToken cancellationToken) =>
            inner.ValidateSegment(entry, cancellationToken);
        public long GetSegmentBytes(SegmentId segmentId) => inner.GetSegmentBytes(segmentId);
        public long? GetAvailableBytes() => inner.GetAvailableBytes();
        public void EnsureSnapshotMetadata() => inner.EnsureSnapshotMetadata();
        public StorageReadSnapshot OpenReadSnapshot() => inner.OpenReadSnapshot();
        public StorageReadSnapshot OpenReadSnapshotForValidation() =>
            inner.OpenReadSnapshotForValidation();
        public StorageStats GetStorageStats() => inner.GetStorageStats();
        public void ReclaimUnreferencedSegments(CancellationToken cancellationToken) =>
            inner.ReclaimUnreferencedSegments(cancellationToken);

        private sealed class BlockingWriteSession(
            IStorageWriteSession innerSession,
            ManualResetEventSlim segmentPublished,
            ManualResetEventSlim allowManifestPublication) : IStorageWriteSession
        {
            public CorpusManifest? CurrentManifest => innerSession.CurrentManifest;

            public SegmentManifestEntry PublishSegment<TDocument>(
                IReadOnlyList<TDocument> documents,
                SegmentId segmentId,
                long minimumFreeBytesAfterPublication,
                CancellationToken cancellationToken)
                where TDocument : ISegmentBuildDocument
            {
                var entry = innerSession.PublishSegment(
                    documents,
                    segmentId,
                    minimumFreeBytesAfterPublication,
                    cancellationToken);
                segmentPublished.Set();
                allowManifestPublication.Wait(cancellationToken);
                return entry;
            }

            public void PublishManifest(CorpusManifest manifest, long minimumFreeBytesAfterPublication) =>
                innerSession.PublishManifest(manifest, minimumFreeBytesAfterPublication);
            public bool CanReplaceSegments() => innerSession.CanReplaceSegments();
            public void EnsureReplacementAllowed() => innerSession.EnsureReplacementAllowed();
            public void PrepareFullBuild(bool discardCorruptState) =>
                innerSession.PrepareFullBuild(discardCorruptState);
            public void CleanupFailedMutation(IReadOnlyCollection<SegmentId> attemptedSegmentIds) =>
                innerSession.CleanupFailedMutation(attemptedSegmentIds);
            public void Dispose() => innerSession.Dispose();
        }
    }
}




