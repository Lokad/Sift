using System.Buffers.Binary;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Tests;

public sealed class ManifestValidationTests
{
    [Fact]
    public void FormatMaximumMetadataRemainsReadable()
    {
        using var fixture = new CorpusFixture();
        Directory.CreateDirectory(fixture.CorpusPath);
        var path = Path.Combine(fixture.CorpusPath, "manifest.bin");
        var metadata = Enumerable.Repeat((byte)0xAB, SegmentFormat.MaximumCommitMetadataBytes).ToArray();
        var manifest = new CorpusManifest(CorpusId.New(), new ContentRevision(7), new StorageGeneration(9), metadata, []);
        File.WriteAllBytes(path, CorpusManifestCodec.Encode(manifest));
        var decoded = CorpusManifestCodec.Read(path);
        Assert.Equal(manifest.CorpusId, decoded.CorpusId);
        Assert.Equal(manifest.ContentRevision, decoded.ContentRevision);
        Assert.Equal(manifest.Generation, decoded.Generation);
        Assert.Equal(metadata, decoded.CommitMetadata);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(71)]
    [InlineData(72)]
    [InlineData(79)]
    public void TruncatedCurrentHeaderOrChecksumIsRejected(int length)
    {
        using var fixture = new CorpusFixture();
        Directory.CreateDirectory(fixture.CorpusPath);
        var path = Path.Combine(fixture.CorpusPath, "manifest.bin");
        var bytes = CorpusManifestCodec.Encode(new CorpusManifest(CorpusId.New(), new StorageGeneration(1), []));
        File.WriteAllBytes(path, bytes.AsSpan(0, length).ToArray());
        Assert.Throws<InvalidDataException>(() => CorpusManifestCodec.Read(path));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(2, 65536)]
    [InlineData(3, 65536)]
    [InlineData(4, 65536)]
    public async Task TrailingDataIsRejectedWithoutChangingValidLegacyBehavior(int version, int tailBytes)
    {
        using var fixture = new CorpusFixture(("a.txt", "alpha\n"), ("b.txt", "café\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
            await index.Update(new InMemoryDocumentSource([]), [new DocumentKey("a.txt")]);
        }
        var path = Path.Combine(fixture.CorpusPath, "manifest.bin");
        RewriteVersion(path, version);
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Validate();
            using var snapshot = index.OpenSnapshot();
            Assert.False(snapshot.TryGetDocumentBytes(new DocumentKey("a.txt"), out _));
            Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("b.txt"), out _));
        }
        using (var stream = new FileStream(path, FileMode.Append)) stream.Write(new byte[tailBytes]);
        using var corrupted = new SiftIndex(fixture.CorpusPath);
        Assert.True(corrupted.RequiresRebuild);
        await Assert.ThrowsAsync<InvalidDataException>(() => corrupted.Validate());
    }

    [Theory]
    [InlineData("tail")]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("bounds")]
    public void MalformedLargeFilesAreRejectedBeforeFileSizedAllocation(string field)
    {
        using var fixture = new CorpusFixture();
        Directory.CreateDirectory(fixture.CorpusPath);
        var path = Path.Combine(fixture.CorpusPath, "manifest.bin");
        var bytes = CorpusManifestCodec.Encode(new CorpusManifest(CorpusId.New(), new StorageGeneration(1), []));
        File.WriteAllBytes(path, bytes);
        _ = CorpusManifestCodec.Read(path);
        if (field == "magic") bytes[0] ^= 1;
        if (field == "version") BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), int.MaxValue);
        if (field == "bounds") BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44), int.MaxValue);
        File.WriteAllBytes(path, bytes);
        using (var file = File.OpenWrite(path)) file.SetLength(64 * 1024 * 1024);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var failure = Record.Exception(() => CorpusManifestCodec.Read(path));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsType<InvalidDataException>(failure);
        Assert.True(allocated < 1024 * 1024, $"Malformed header allocated {allocated} bytes before rejection.");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ValidManifestsSupportUpdateAndCompaction(int version)
    {
        using var fixture = new CorpusFixture(("a.txt", "alpha\n"), ("b.txt", "café\n"));
        using (var index = new SiftIndex(fixture.CorpusPath))
        {
            await index.Build(fixture.CreateSource());
            await index.Update(new InMemoryDocumentSource([]), [new DocumentKey("a.txt")]);
        }
        RewriteVersion(Path.Combine(fixture.CorpusPath, "manifest.bin"), version);
        using var reopened = new SiftIndex(fixture.CorpusPath);
        await reopened.Validate();
        using var retained = reopened.OpenSnapshot();
        await reopened.Update(new InMemoryDocumentSource([("c.txt", "beta")]), []);
        await reopened.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
        await reopened.Validate();
        Assert.True(retained.TryGetDocumentBytes(new DocumentKey("b.txt"), out _));
        Assert.False(retained.TryGetDocumentBytes(new DocumentKey("c.txt"), out _));
        using var current = reopened.OpenSnapshot();
        Assert.True(current.TryGetDocumentBytes(new DocumentKey("b.txt"), out _));
        Assert.True(current.TryGetDocumentBytes(new DocumentKey("c.txt"), out _));
    }

    private static void RewriteVersion(string path, int version)
    {
        if (version == 4) return;
        var current = File.ReadAllBytes(path);
        var count = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(36));
        var oldTableLength = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(44));
        var entrySize = version == 2 ? 28 : 36;
        var tableLength = oldTableLength - count * (36 - entrySize);
        var checksum = 56 + tableLength;
        var bytes = new byte[checksum + 8];
        current.AsSpan(0, 56).CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), 56);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44), tableLength);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48), checksum);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(52), 8);
        var read = 72;
        var write = 56;
        for (var i = 0; i < count; i++)
        {
            var deadBytes = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(read + 24));
            current.AsSpan(read, entrySize).CopyTo(bytes.AsSpan(write));
            current.AsSpan(read + 36, deadBytes).CopyTo(bytes.AsSpan(write + entrySize));
            read += 36 + deadBytes;
            write += entrySize + deadBytes;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(checksum), Hashing.Fnv1A64(bytes.AsSpan(0, checksum)));
        File.WriteAllBytes(path, bytes);
    }
}
