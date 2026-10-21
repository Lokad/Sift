using System.Buffers;

namespace Lokad.Sift.Tests;

public sealed class ContentMemoryFailureTests
{
    [Theory]
    [InlineData(DocumentContentLifetime.UntilNextRead)]
    [InlineData(DocumentContentLifetime.UntilOperationCompletes)]
    public async Task OverlayContentFailuresPreserveIdentityAndRemainUsable(DocumentContentLifetime lifetime)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("target.txt", "needle")]));
        using var overlay = index.OpenOverlaySnapshot();
        foreach (var failAt in new[] { 1, 2 })
        {
            var failure = new EndOfStreamException("Caller overlay memory failed.");
            using var memory = new Buffer("changed"u8.ToArray(), failure, failAt);
            var revision = (await overlay.Update(new InMemoryDocumentSource([]), [])).CommittedRevision;
            var caught = await Record.ExceptionAsync(() => overlay.Update(new Source(memory.View, lifetime), []));
            Assert.Same(failure, caught);
            Assert.Equal(revision, (await overlay.Update(new InMemoryDocumentSource([]), [])).CommittedRevision);
            Assert.True(overlay.TryGetDocumentBytes(new DocumentKey("target.txt"), out var bytes));
            Assert.Equal("needle"u8.ToArray(), bytes.ToArray());
            Assert.False(overlay.TryGetDocumentBytes(new DocumentKey("staged.txt"), out _));
        }
        await overlay.Update(new InMemoryDocumentSource([("next.txt", "next")]), []);
        Assert.True(overlay.TryGetDocumentBytes(new DocumentKey("next.txt"), out _));
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var disk in new[] { false, true })
        foreach (var build in new[] { false, true })
        foreach (var asyncSource in new[] { false, true })
        foreach (var unchanged in new[] { false, true })
        foreach (var lifetime in Enum.GetValues<DocumentContentLifetime>())
            yield return [disk, build, asyncSource, unchanged, lifetime];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CallerMemoryFailuresPreserveCommittedIndex(bool disk, bool build, bool asyncSource, bool unchanged, DocumentContentLifetime lifetime)
    {
        // Discover actual public-operation reads instead of fixing tests to a count.
        var successfulReads = await Check(0);
        for (var failAt = 1; failAt <= successfulReads; failAt++) await Check(failAt);

        async Task<int> Check(int failAt)
        {
            using var fixture = new CorpusFixture();
            var options = new SiftOptions { TargetSegmentDocumentCount = 1 };
            using var index = disk ? new SiftIndex(fixture.CorpusPath, options) : SiftIndex.CreateInMemory(options);
            await index.Build(new InMemoryDocumentSource([("target.txt", "needle")]),
                new BuildOptions { CommitMetadataProvider = _ => "checkpoint"u8.ToArray() });
            var before = index.GetIndexState();
            using var retained = index.OpenSnapshot();
            var failure = new InvalidDataException("Caller content failure.");
            using var memory = new Buffer(unchanged ? "needle"u8.ToArray() : "changed"u8.ToArray(), failure, failAt);
            var source = new Source(memory.View, lifetime);
            var caught = await Record.ExceptionAsync(async () =>
            {
                if (asyncSource)
                {
                    if (build) await index.Build((IAsyncEnumerable<SubmittedDocument>)source);
                    else await index.Update((IAsyncEnumerable<SubmittedDocument>)source, []);
                }
                else if (build) await index.Build((IDocumentSource)source);
                else await index.Update((IDocumentSource)source, []);
            });
            if (failAt == 0) { Assert.Null(caught); return memory.Reads; }
            Assert.Same(failure, caught);
            Assert.False(index.RequiresRebuild);
            Assert.Equal(before.Identity, index.GetIndexIdentity());
            Assert.Equal(before.CommitMetadata.ToArray(), index.GetIndexState().CommitMetadata.ToArray());
            using var fresh = index.OpenSnapshot();
            foreach (var snapshot in new[] { retained, fresh })
            {
                Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("target.txt"), out var bytes));
                Assert.Equal("needle"u8.ToArray(), bytes.ToArray());
                Assert.False(snapshot.TryGetDocumentBytes(new DocumentKey("staged.txt"), out _));
            }
            Assert.Equal(1, index.GetStorageStats().PhysicalSegmentCount);
            Assert.Equal(0, index.GetStorageStats().TemporaryBytes);
            await index.Validate();
            if (disk)
            {
                using var reopened = new SiftIndex(fixture.CorpusPath);
                Assert.Equal(before.Identity, reopened.GetIndexIdentity());
                await reopened.Validate();
            }
            await index.Update(new InMemoryDocumentSource([("next.txt", "next")]), []);
            return memory.Reads;
        }
    }

    private sealed class Source(ReadOnlyMemory<byte> content, DocumentContentLifetime lifetime) : IDocumentSource, IAsyncEnumerable<SubmittedDocument>
    {
        private int reads;
        public bool TryReadNext(out SubmittedDocument document)
        {
            document = reads++ switch
            {
                0 => new SubmittedDocument("staged.txt", "staged"u8.ToArray()),
                1 => new SubmittedDocument("target.txt", content, null, lifetime),
                _ => default
            };
            return reads <= 2;
        }
        public async IAsyncEnumerator<SubmittedDocument> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            while (TryReadNext(out var document)) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); yield return document; }
        }
    }
    private sealed class Buffer(byte[] bytes, Exception failure, int failAt) : MemoryManager<byte>
    {
        public int Reads;
        public ReadOnlyMemory<byte> View => CreateMemory(bytes.Length);
        public override Span<byte> GetSpan() { if (++Reads == failAt) throw failure; return bytes; }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
