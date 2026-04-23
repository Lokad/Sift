using System.Text;
using System.Buffers.Binary;
using System.Diagnostics;
using Lokad.Sift;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Search;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Tests;

public sealed class StorageIntegrityTests
{
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
        using (var stream = new FileStream(segmentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = stream.Length / 2;
            var current = stream.ReadByte();
            stream.Position = stream.Position - 1;
            stream.WriteByte(unchecked((byte)(current ^ 0x5A)));
        }

        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = storage.ReadManifest();
        Assert.Throws<InvalidDataException>(() => storage.OpenSegment(manifest.Segments[0].SegmentId));
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
        var manifest = new CorpusManifest(Guid.NewGuid(), 7, []);
        storage.PublishManifest(manifest);
        File.Move(storage.ManifestPath, storage.ManifestPath + ".tmp", overwrite: true);

        storage.EnsureLayout();

        Assert.True(File.Exists(storage.ManifestPath));
        Assert.False(File.Exists(storage.ManifestPath + ".tmp"));
        var recovered = storage.ReadManifest();
        Assert.Equal(7UL, recovered.Generation);
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
    public async Task ManifestDeadOrdinalsRoundTripThroughCompressedEncoding()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = new CorpusManifest(
            Guid.NewGuid(),
            3,
            [
                new SegmentManifestEntry(42, 10, 100, [1, 2, 3, 1000, 5000])
            ]);

        storage.PublishManifest(manifest);
        var roundTripped = storage.ReadManifest();

        Assert.Equal(manifest.Generation, roundTripped.Generation);
        Assert.Equal(manifest.Segments[0].DeadOrdinals, roundTripped.Segments[0].DeadOrdinals);
        Assert.True(new FileInfo(storage.ManifestPath).Length < 56 + 28 + 8 + manifest.Segments[0].DeadOrdinals.Length * 4);
    }

    [Fact]
    public async Task MappedSegmentTracksOpenMappedBytes()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = storage.ReadManifest();
        var baselineBytes = MappedSegment.CurrentMappedBytes;
        var baselineOpen = MappedSegment.CurrentOpenSegments;

        using (var segment = storage.OpenSegment(manifest.Segments[0].SegmentId))
        {
            Assert.True(MappedSegment.CurrentMappedBytes > baselineBytes);
            Assert.True(MappedSegment.CurrentOpenSegments >= baselineOpen + 1);
        }

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while ((MappedSegment.CurrentMappedBytes > baselineBytes || MappedSegment.CurrentOpenSegments > baselineOpen) &&
               Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.True(MappedSegment.CurrentMappedBytes <= baselineBytes);
        Assert.True(MappedSegment.CurrentOpenSegments <= baselineOpen);
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
    public async Task StatsRegionStoresByteFrequencyHistogram()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "aba\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var storage = new CorpusStorage(fixture.CorpusPath);
        var manifest = storage.ReadManifest();
        using var segment = storage.OpenSegment(manifest.Segments[0].SegmentId);

        Assert.Equal(2, segment.GetByteFrequency((byte)'a'));
        Assert.Equal(1, segment.GetByteFrequency((byte)'b'));
        Assert.Equal(1, segment.GetByteFrequency((byte)'\n'));
    }

    [Fact]
    public async Task SegmentFormatReservesFutureSidecarRegionIds()
    {
        Assert.Equal(17, SegmentFormat.RegionCount);
        Assert.Equal(SegmentFormat.RegionCount + 1, SegmentFormat.ReservedRegionIdStart);
        Assert.Equal((int)SegmentRegionId.ReservedSidecar08, SegmentFormat.ReservedRegionIdEnd);

        for (var regionId = SegmentFormat.ReservedRegionIdStart; regionId <= SegmentFormat.ReservedRegionIdEnd; regionId++)
        {
            Assert.True(SegmentFormat.IsReservedRegionId((SegmentRegionId)regionId));
        }

        Assert.False(SegmentFormat.IsReservedRegionId(SegmentRegionId.Stats));
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
        Assert.Throws<IOException>(() => storage.PublishSegment(documents, 0x1234UL, CancellationToken.None));

        Assert.Empty(Directory.EnumerateDirectories(storage.SegmentsPath));
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(storage.TempPath));

        storage.EnsureLayout();

        Assert.Empty(Directory.EnumerateDirectories(storage.SegmentsPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TempPath));
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

        var next = new CorpusManifest(original.CorpusId, original.Generation + 1, original.Segments);
        Assert.Throws<IOException>(() => storage.PublishManifest(next));

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

        var manifest = new CorpusManifest(Guid.NewGuid(), 1, []);
        Assert.Throws<IOException>(() => storage.PublishManifest(manifest));

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
        var compactedEntry = storage.PublishSegment(
            [CreateStoredDocument("src/a.txt", "omega\n")],
            0x7777UL,
            CancellationToken.None);

        storage.FaultInjector = new PublicationFaultInjector
        {
            ThrowOnStage = PublicationStage.AfterManifestFlushBeforeSwap
        };

        var failedManifest = new CorpusManifest(original.CorpusId, original.Generation + 1, [compactedEntry]);
        Assert.Throws<IOException>(() => storage.PublishManifest(failedManifest));

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

    private static StoredDocument CreateStoredDocument(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var lineStarts = LineMap.BuildLineStarts(bytes);
        return new StoredDocument
        {
            Path = path,
            PathUtf8 = Encoding.UTF8.GetBytes(path),
            Content = bytes,
            LineCount = lineStarts.Length,
            EncodedLineIndex = LineMap.EncodeLineIndex(lineStarts),
            Flags = 0,
            MTimeUtcTicks = 0,
            ContentHash64 = Hashing.Fnv1A64(bytes)
        };
    }
}




