namespace Lokad.Sift.Tests;

public sealed class PublicationReserveTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    public async Task InitialBuildChecksExactPostPublicationBoundary(bool withPack, int headroomOffset)
    {
        var rows = withPack ? new[] { ("a.txt", "alpha") } : [];
        using var reference = new CorpusFixture();
        using (var seed = new SiftIndex(reference.CorpusPath, new SiftOptions { MinimumFreeBytesAfterMutation = 0 }))
            await seed.Build(new InMemoryDocumentSource(rows), new BuildOptions { CommitMetadataProvider = _ => new byte[1024] });
        var finalBytes = Directory.EnumerateFiles(reference.CorpusPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions { MaximumPhysicalBytes = finalBytes + 8192 + headroomOffset, MinimumFreeBytesAfterMutation = 8192 });
        var build = index.Build(new InMemoryDocumentSource(rows), new BuildOptions { CommitMetadataProvider = _ => new byte[1024] });
        if (headroomOffset < 0)
        {
            await Assert.ThrowsAsync<IOException>(() => build);
            Assert.False(File.Exists(Path.Combine(fixture.CorpusPath, "manifest.bin")));
        }
        else
        {
            await build;
            Assert.Equal(8192, index.GetStorageStats().AvailableBytes);
            await index.Validate();
        }
    }

    [Fact]
    public async Task MetadataSwapCreditsOldManifestButRetainsPinnedLease()
    {
        using var fixture = new CorpusFixture();
        using (var seed = new SiftIndex(fixture.CorpusPath, new SiftOptions { MinimumFreeBytesAfterMutation = 0 }))
            await seed.Build(new InMemoryDocumentSource([]), new BuildOptions { CommitMetadataProvider = _ => new byte[4096] });
        var beforeBytes = Directory.EnumerateFiles(fixture.CorpusPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        var leaseBytes = new FileInfo(Directory.GetFiles(fixture.CorpusPath, "*.lease", SearchOption.AllDirectories).Single()).Length;
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions { MaximumPhysicalBytes = beforeBytes + 4096 + leaseBytes + 8192, MinimumFreeBytesAfterMutation = 8192 });
        using var retained = index.OpenSnapshot();
        await index.Update(new InMemoryDocumentSource([]), [], new UpdateOptions { CommitMetadataProvider = _ => new byte[8192] });
        Assert.Equal(4096, retained.CommitMetadata.Length);
        Assert.Equal(8192, index.GetIndexState().CommitMetadata.Length);
        Assert.Equal(8192, index.GetStorageStats().AvailableBytes);
        Assert.Equal(2, Directory.GetFiles(fixture.CorpusPath, "*.lease", SearchOption.AllDirectories).Length);
        await index.Validate();
    }

    [Fact]
    public async Task OldManifestCreditDoesNotWeakenTemporaryFileQuota()
    {
        using var fixture = new CorpusFixture();
        using (var seed = new SiftIndex(fixture.CorpusPath, new SiftOptions { MinimumFreeBytesAfterMutation = 0 }))
            await seed.Build(new InMemoryDocumentSource([]), new BuildOptions { CommitMetadataProvider = _ => new byte[12288] });
        var beforeBytes = Directory.EnumerateFiles(fixture.CorpusPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions { MaximumPhysicalBytes = beforeBytes + 512, MinimumFreeBytesAfterMutation = 0 });
        var before = index.GetIndexState();
        await Assert.ThrowsAsync<IOException>(() => index.Update(new InMemoryDocumentSource([]), [],
            new UpdateOptions { CommitMetadataProvider = _ => Enumerable.Repeat((byte)1, 12288).ToArray() }));
        Assert.Equal(before.Identity, index.GetIndexIdentity());
        Assert.Equal(before.CommitMetadata.ToArray(), index.GetIndexState().CommitMetadata.ToArray());
        Assert.False(index.RequiresRebuild);
        await index.Validate();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MetadataPublicationRetainsMutationReserve(bool build, bool withPack)
    {
        using var fixture = new CorpusFixture();
        var rows = withPack ? new[] { ("old.txt", "alpha") } : [];
        using (var seed = new SiftIndex(fixture.CorpusPath, new SiftOptions { MinimumFreeBytesAfterMutation = 0 }))
            await seed.Build(new InMemoryDocumentSource(rows));
        var originalBytes = Directory.EnumerateFiles(fixture.CorpusPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        var packBytes = withPack ? new FileInfo(Directory.GetFiles(Path.Combine(fixture.CorpusPath, "segments"), "segment.pack", SearchOption.AllDirectories).Single()).Length : 0;
        const long reserve = 8192;
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            MaximumPhysicalBytes = originalBytes + packBytes + reserve + 1024,
            MinimumFreeBytesAfterMutation = reserve
        });
        var before = index.GetIndexState();
        using var retained = index.OpenSnapshot();
        var source = new InMemoryDocumentSource(withPack ? [("new.txt", "alpha")] : []);
        var failure = await Assert.ThrowsAsync<IOException>(async () =>
        {
            if (build) await index.Build(source, new BuildOptions { CommitMetadataProvider = _ => new byte[4096] });
            else await index.Update(source, [], new UpdateOptions { CommitMetadataProvider = _ => new byte[4096] });
        });
        Assert.Contains("retaining", failure.Message);
        Assert.False(index.RequiresRebuild);
        Assert.Equal(before.Identity, index.GetIndexIdentity());
        Assert.Equal(before.CommitMetadata.ToArray(), index.GetIndexState().CommitMetadata.ToArray());
        using var fresh = index.OpenSnapshot();
        Assert.Equal(withPack, retained.TryGetDocumentBytes(new DocumentKey("old.txt"), out _));
        Assert.Equal(withPack, fresh.TryGetDocumentBytes(new DocumentKey("old.txt"), out _));
        Assert.False(fresh.TryGetDocumentBytes(new DocumentKey("new.txt"), out _));
        Assert.Equal(withPack ? 1 : 0, index.GetStorageStats().PhysicalSegmentCount);
        Assert.Equal(0, index.GetStorageStats().TemporaryBytes);
        await index.Validate();
        await index.Update(new InMemoryDocumentSource([]), [], new UpdateOptions { CommitMetadataProvider = _ => "ok"u8.ToArray() });
        Assert.True(index.GetStorageStats().AvailableBytes >= reserve);
    }

    [Fact]
    public async Task EmptyInitialBuildRetainsReserveForMetadata()
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions { MaximumPhysicalBytes = 16384, MinimumFreeBytesAfterMutation = 8192 });
        await Assert.ThrowsAsync<IOException>(() => index.Build(new InMemoryDocumentSource([]),
            new BuildOptions { CommitMetadataProvider = _ => new byte[12288] }));
        Assert.False(File.Exists(Path.Combine(fixture.CorpusPath, "manifest.bin")));
        Assert.False(index.RequiresRebuild);
        Assert.Equal(0, index.GetStorageStats().TemporaryBytes);
        await index.Build(new InMemoryDocumentSource([]));
        Assert.True(index.GetStorageStats().AvailableBytes >= 8192);
        await index.Validate();
    }

    [Fact]
    public async Task CleanupUpdatesAndFullyDeadCompactionRemainPossibleBelowReserve()
    {
        using var fixture = new CorpusFixture();
        using (var seed = new SiftIndex(fixture.CorpusPath, new SiftOptions { MinimumFreeBytesAfterMutation = 0 }))
            await seed.Build(new InMemoryDocumentSource([("old.txt", "alpha")]),
                new BuildOptions { CommitMetadataProvider = _ => new byte[4096] });
        var bytes = Directory.EnumerateFiles(fixture.CorpusPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions { MaximumPhysicalBytes = bytes + 8192, MinimumFreeBytesAfterMutation = bytes });
        var before = index.GetIndexIdentity();
        await index.Update(new InMemoryDocumentSource([]), []);
        Assert.Equal(before, index.GetIndexIdentity());
        await index.Update(new InMemoryDocumentSource([]), [], new UpdateOptions { CommitMetadataProvider = _ => new byte[1024] });
        Assert.True(index.GetStorageStats().AvailableBytes < bytes);
        await index.Update(new InMemoryDocumentSource([]), [new DocumentKey("old.txt")]);
        var compacted = await index.Compact(new CompactOptions { MinimumFreeBytesAfterCompaction = long.MaxValue });
        Assert.Equal(0, compacted.OutputSegments);
        Assert.Equal(0, index.GetStorageStats().PhysicalSegmentCount);
        await index.Validate();
    }
}
