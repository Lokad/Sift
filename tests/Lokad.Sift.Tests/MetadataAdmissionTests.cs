using System.Buffers;

namespace Lokad.Sift.Tests;

public sealed class MetadataAdmissionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OversizedMetadataIsRejectedBeforeAccessingItsBytes(bool disk, bool build)
    {
        using var fixture = new CorpusFixture();
        var options = new SiftOptions { MaxCommitMetadataBytes = 5 };
        using var index = disk ? new SiftIndex(fixture.CorpusPath, options) : SiftIndex.CreateInMemory(options);
        await index.Build(new InMemoryDocumentSource([("old.txt", "alpha")]),
            new BuildOptions { CommitMetadataProvider = _ => "old"u8.ToArray() });
        var before = index.GetIndexState();
        using var retained = index.OpenSnapshot();
        using var memory = new InaccessibleMemory(64 * 1024 * 1024, new InvalidDataException("Bytes must not be accessed."));
        var providerCalls = 0;
        ReadOnlyMemory<byte> Metadata() { providerCalls++; return memory.View; }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(index, build, Metadata));

        Assert.Contains("commit metadata exceeds the configured 5 byte limit", failure.Message);
        Assert.Equal(1, providerCalls);
        Assert.Equal(0, memory.SpanReads);
        Assert.Equal(before.Identity, index.GetIndexIdentity());
        Assert.Equal(before.CommitMetadata.ToArray(), index.GetIndexState().CommitMetadata.ToArray());
        Assert.True(retained.TryGetDocumentBytes(new DocumentKey("old.txt"), out _));
        using var current = index.OpenSnapshot();
        Assert.True(current.TryGetDocumentBytes(new DocumentKey("old.txt"), out _));
        Assert.False(current.TryGetDocumentBytes(new DocumentKey("new.txt"), out _));
        Assert.Equal(1, index.GetStorageStats().PhysicalSegmentCount);
        Assert.Equal(0, index.GetStorageStats().TemporaryBytes);
        await index.Validate();
        Assert.False(index.RequiresRebuild);
        await Apply(index, build, () => "next"u8.ToArray());
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 5)]
    [InlineData(false, true, 5)]
    [InlineData(true, false, 5)]
    [InlineData(true, true, 5)]
    public async Task MetadataAtTheLimitIsAcceptedAndCopied(bool disk, bool build, int limit)
    {
        using var fixture = new CorpusFixture();
        var options = new SiftOptions { MaxCommitMetadataBytes = limit };
        using var index = disk ? new SiftIndex(fixture.CorpusPath, options) : SiftIndex.CreateInMemory(options);
        await index.Build(new InMemoryDocumentSource([]));
        var metadata = Enumerable.Repeat((byte)7, limit).ToArray();

        await Apply(index, build, () => metadata);
        Array.Fill(metadata, (byte)9);

        Assert.Equal(Enumerable.Repeat((byte)7, limit), index.GetIndexState().CommitMetadata.ToArray());
        using var snapshot = index.OpenSnapshot();
        Assert.Equal(Enumerable.Repeat((byte)7, limit), snapshot.CommitMetadata.ToArray());
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("new.txt"), out _));
        await index.Validate();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AdmittedMetadataAccessFailuresPreserveCallerExceptionIdentity(bool disk, bool build)
    {
        using var fixture = new CorpusFixture();
        var options = new SiftOptions { MaxCommitMetadataBytes = 5 };
        using var index = disk ? new SiftIndex(fixture.CorpusPath, options) : SiftIndex.CreateInMemory(options);
        await index.Build(new InMemoryDocumentSource([]));
        var before = index.GetIndexIdentity();
        var failure = new InvalidDataException("Caller memory failure.");
        using var memory = new InaccessibleMemory(5, failure);

        var caught = await Record.ExceptionAsync(() => Apply(index, build, () => memory.View));

        Assert.Same(failure, caught);
        Assert.Equal(before, index.GetIndexIdentity());
        Assert.False(index.RequiresRebuild);
        await index.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task ExistingMetadataStillRespectsALowerLimitWithoutAProvider(int limit)
    {
        using var fixture = new CorpusFixture();
        using (var writer = new SiftIndex(fixture.CorpusPath))
            await writer.Build(new InMemoryDocumentSource([("old.txt", "alpha")]),
                new BuildOptions { CommitMetadataProvider = _ => "original"u8.ToArray() });
        using var restricted = new SiftIndex(fixture.CorpusPath, new SiftOptions { MaxCommitMetadataBytes = limit });
        var before = restricted.GetIndexState();

        await Assert.ThrowsAsync<InvalidOperationException>(() => restricted.Update(
            new InMemoryDocumentSource([("new.txt", "beta")]), []));

        Assert.Equal(before.Identity, restricted.GetIndexIdentity());
        Assert.Equal(before.CommitMetadata.ToArray(), restricted.GetIndexState().CommitMetadata.ToArray());
        Assert.False(restricted.RequiresRebuild);
        await restricted.Update(new InMemoryDocumentSource([("new.txt", "beta")]), [],
            new UpdateOptions { CommitMetadataProvider = _ => ReadOnlyMemory<byte>.Empty });
        Assert.Empty(restricted.GetIndexState().CommitMetadata.ToArray());
        await restricted.Validate();
    }

    private static async Task Apply(SiftIndex index, bool build, Func<ReadOnlyMemory<byte>> metadata)
    {
        var source = new InMemoryDocumentSource([("new.txt", "beta")]);
        if (build) await index.Build(source, new BuildOptions { CommitMetadataProvider = _ => metadata() });
        else await index.Update(source, [], new UpdateOptions { CommitMetadataProvider = _ => metadata() });
    }

    private sealed class InaccessibleMemory(int length, Exception failure) : MemoryManager<byte>
    {
        public ReadOnlyMemory<byte> View => CreateMemory(length);
        public int SpanReads { get; private set; }
        public override Span<byte> GetSpan() { SpanReads++; throw failure; }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
