using System.Buffers.Binary;
using System.Text;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Tests;

public sealed class StorageCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedVersion3PacksSupportSearchUpdatesAndCompaction(bool shortStats)
    {
        using var fixture = CreateFixture();
        var path = await InstallReleasedPack(fixture, shortStats);
        var originalBytes = File.ReadAllBytes(path);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Validate();
        Assert.False(index.RequiresRebuild);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));

        using (var oldSnapshot = index.OpenSnapshot())
        {
            var hits = new HitBuffer();
            var stats = oldSnapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new(), hits);
            Assert.Equal(2, stats.HitsReturned);
            Assert.All(hits.Hits, hit => Assert.Equal("alpha", oldSnapshot.Materialize(hit, 0, 0).MatchText));

            await index.Update(new InMemoryDocumentSource([("src/a.txt", "alpha updated\n")]), [new DocumentKey("src/empty.txt")]);
            Assert.True(oldSnapshot.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out var oldText));
            Assert.Equal("alpha one\n", Encoding.UTF8.GetString(oldText.Span));
            Assert.True(oldSnapshot.TryGetDocumentBytes(new DocumentKey("src/empty.txt"), out _));

            using var current = index.OpenSnapshot();
            Assert.True(current.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out var newText));
            Assert.Equal("alpha updated\n", Encoding.UTF8.GetString(newText.Span));
            Assert.False(current.TryGetDocumentBytes(new DocumentKey("src/empty.txt"), out _));
            Assert.Equal(1, current.Search(new SearchQuery("CAFÉ", PatternMode.Regex, CaseMode.Insensitive), new(), new HitBuffer()).HitsReturned);
            await index.Validate();
            Assert.Equal(new[] { 3, 4 }, SegmentPaths(fixture).Select(ReadVersion).Order().ToArray());
        }

        var compact = await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
        Assert.True(compact.InputSegments > 0);
        Assert.All(SegmentPaths(fixture), pack => Assert.Equal(4, ReadVersion(pack)));
        await index.Validate();
        Assert.False(index.RequiresRebuild);
        using var compacted = index.OpenSnapshot();
        Assert.Equal(2, compacted.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new(), new HitBuffer()).HitsReturned);
        Assert.Equal(1, compacted.Search(new SearchQuery("CAFÉ", PatternMode.Regex, CaseMode.Insensitive), new(), new HitBuffer()).HitsReturned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasedStatisticsRetainTheirUnicodeEstimate(bool shortStats)
    {
        using var segment = new MappedSegment(ReadReleasedPack(shortStats), validateChecksums: true, CancellationToken.None);
        var stats = segment.GetNonAsciiDocumentStats();
        Assert.Equal(shortStats ? segment.DocumentCount : 1, stats.DocumentCount);
        Assert.Equal(shortStats ? segment.ContentBytes : "café alpha\n"u8.Length, stats.ContentBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteBytesAreIgnoredButStillChecksummed(bool repairChecksum)
    {
        using var fixture = CreateFixture();
        var path = await InstallReleasedPack(fixture, shortStats: false);
        var bytes = File.ReadAllBytes(path);
        var (offset, length) = StatsRegion(bytes);
        // Deliberately invalid as a frequency counter: it has no semantic role now.
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8), int.MinValue);
        if (repairChecksum) UpdateStatsChecksum(bytes, offset, length);
        File.WriteAllBytes(path, bytes);

        using var index = new SiftIndex(fixture.CorpusPath);
        if (repairChecksum)
        {
            await index.Validate();
            Assert.False(index.RequiresRebuild);
            using var snapshot = index.OpenSnapshot();
            Assert.Equal(2, snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new(), new HitBuffer()).HitsReturned);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => index.Validate());
            Assert.True(index.RequiresRebuild);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(1032)]
    [InlineData(1036)]
    public async Task RetainedVersion3StatisticsAreStillValidated(int fieldOffset)
    {
        using var fixture = CreateFixture();
        var path = await InstallReleasedPack(fixture, shortStats: false);
        var bytes = File.ReadAllBytes(path);
        var (offset, length) = StatsRegion(bytes);
        if (fieldOffset == 1036)
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + fieldOffset), -1);
        else
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + fieldOffset), -1);
        UpdateStatsChecksum(bytes, offset, length);
        File.WriteAllBytes(path, bytes);

        using var index = new SiftIndex(fixture.CorpusPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => index.Validate());
        Assert.True(index.RequiresRebuild);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Version4RejectsVersion3StatisticsLayouts(bool shortStats)
    {
        var bytes = ReadReleasedPack(shortStats);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 4);
        Assert.Throws<InvalidDataException>(() => new MappedSegment(bytes, validateChecksums: true, CancellationToken.None));
    }

    private static CorpusFixture CreateFixture() => new(
        ("src/a.txt", "alpha one\n"), ("src/b.txt", "beta two\n"),
        ("src/empty.txt", ""), ("src/unicode.txt", "café alpha\n"));

    private static async Task<string> InstallReleasedPack(CorpusFixture fixture, bool shortStats)
    {
        using (var index = new SiftIndex(fixture.CorpusPath))
            await index.Build(fixture.CreateSource());
        var path = SegmentPaths(fixture).Single();
        var segmentId = BinaryPrimitives.ReadUInt64LittleEndian(File.ReadAllBytes(path).AsSpan(12));
        var bytes = ReadReleasedPack(shortStats);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12), segmentId);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string[] SegmentPaths(CorpusFixture fixture) => Directory.GetFiles(
        Path.Combine(fixture.CorpusPath, "segments"), "segment.pack", SearchOption.AllDirectories);

    private static int ReadVersion(string path) => BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(path).AsSpan(8));

    private static byte[] ReadReleasedPack(bool shortStats)
    {
        using var input = typeof(StorageCompatibilityTests).Assembly.GetManifestResourceStream(
            "Lokad.Sift.Tests.Fixtures.segment-v3.pack")!;
        using var output = new MemoryStream();
        input.CopyTo(output);
        var bytes = output.ToArray();
        if (!shortStats) return bytes;

        var (offset, length) = StatsRegion(bytes);
        Assert.Equal(1044, length);
        var oldChecksumOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(52)));
        var shortened = new byte[bytes.Length - 12];
        bytes.AsSpan(0, offset + 1032).CopyTo(shortened);
        bytes.AsSpan(oldChecksumOffset).CopyTo(shortened.AsSpan(oldChecksumOffset - 12));
        BinaryPrimitives.WriteInt64LittleEndian(shortened.AsSpan(52), oldChecksumOffset - 12);
        var statsEntry = SegmentFormat.HeaderSize + ((int)SegmentRegionId.Stats - 1) * SegmentFormat.RegionDirectoryEntrySize;
        BinaryPrimitives.WriteInt64LittleEndian(shortened.AsSpan(statsEntry + 12), 1032);
        UpdateStatsChecksum(shortened, offset, 1032);
        return shortened;
    }

    private static (int Offset, int Length) StatsRegion(byte[] bytes)
    {
        var entry = SegmentFormat.HeaderSize + ((int)SegmentRegionId.Stats - 1) * SegmentFormat.RegionDirectoryEntrySize;
        return (checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(entry + 4))),
            checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(entry + 12))));
    }

    private static void UpdateStatsChecksum(byte[] bytes, int offset, int length)
    {
        var table = checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(52)));
        var entry = table + ((int)SegmentRegionId.Stats - 1) * SegmentFormat.ChecksumEntrySize;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 4), Hashing.Fnv1A64(bytes.AsSpan(offset, length)));
    }
}
