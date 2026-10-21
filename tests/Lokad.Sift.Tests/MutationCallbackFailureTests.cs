namespace Lokad.Sift.Tests;

public sealed class MutationCallbackFailureTests
{
    public static IEnumerable<object[]> MutationsAndCallbacks()
    {
        foreach (var disk in new[] { false, true })
        foreach (var build in new[] { false, true })
        foreach (var callback in new[] { "sync", "get-enumerator", "move-next", "current", "dispose", "metadata" })
            yield return [disk, build, callback];
    }

    [Theory]
    [MemberData(nameof(MutationsAndCallbacks))]
    public async Task CallerFailurePreservesCommittedIndex(bool disk, bool build, string callback)
    {
        foreach (var failure in Failures())
        {
            using var fixture = new CorpusFixture();
            var options = new SiftOptions { TargetSegmentDocumentCount = 1 };
            using var index = disk ? new SiftIndex(fixture.CorpusPath, options) : SiftIndex.CreateInMemory(options);
            await index.Build(new InMemoryDocumentSource([("old.txt", "alpha")]),
                new BuildOptions { CommitMetadataProvider = _ => "checkpoint"u8.ToArray() });
            var before = index.GetIndexState();
            using var retained = index.OpenSnapshot();
            var source = new FailingSource(callback, failure);

            var caught = await Record.ExceptionAsync(async () =>
            {
                if (callback == "metadata")
                {
                    if (build)
                        await index.Build(new InMemoryDocumentSource([("new.txt", "beta")]),
                            new BuildOptions { CommitMetadataProvider = _ => ThrowMetadata(failure) });
                    else
                        await index.Update(new InMemoryDocumentSource([("new.txt", "beta")]), [],
                            new UpdateOptions { CommitMetadataProvider = _ => ThrowMetadata(failure) });
                }
                else if (callback == "sync")
                {
                    if (build) await index.Build((IDocumentSource)source);
                    else await index.Update((IDocumentSource)source, []);
                }
                else
                {
                    if (build) await index.Build((IAsyncEnumerable<SubmittedDocument>)source);
                    else await index.Update((IAsyncEnumerable<SubmittedDocument>)source, []);
                }
            });

            Assert.Same(failure, caught);
            Assert.Contains(callback == "metadata" ? nameof(ThrowMetadata) : nameof(FailingSource), failure.StackTrace ?? "");
            Assert.Equal(callback is "sync" or "metadata" or "get-enumerator" ? 0 : 1, source.DisposeCalls);
            Assert.False(index.RequiresRebuild);
            Assert.Equal(before.Identity, index.GetIndexState().Identity);
            Assert.Equal(before.CommitMetadata.ToArray(), index.GetIndexState().CommitMetadata.ToArray());
            AssertOldDocument(retained);
            using (var fresh = index.OpenSnapshot()) AssertOldDocument(fresh);
            Assert.Equal(1, index.GetStorageStats().PhysicalSegmentCount);
            Assert.Equal(0, index.GetStorageStats().TemporaryBytes);
            if (disk)
            {
                using var reopened = new SiftIndex(fixture.CorpusPath);
                Assert.False(reopened.RequiresRebuild);
                Assert.Equal(before.Identity, reopened.GetIndexIdentity());
                using var fresh = reopened.OpenSnapshot();
                AssertOldDocument(fresh);
            }

            await index.Update(new InMemoryDocumentSource([("new.txt", "beta")]), []);
            using var updated = index.OpenSnapshot();
            Assert.True(updated.TryGetDocumentBytes(new DocumentKey("new.txt"), out _));
            AssertOldDocument(retained);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncCancellationIsForwardedAndDisposesSource(bool build)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { TargetSegmentDocumentCount = 1 });
        await index.Build(new InMemoryDocumentSource([("old.txt", "alpha")]));
        var identity = index.GetIndexIdentity();
        using var cancellation = new CancellationTokenSource();
        var source = new FailingSource("cancel", new InvalidOperationException(), cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (build) await index.Build((IAsyncEnumerable<SubmittedDocument>)source, cancellation.Token);
            else await index.Update((IAsyncEnumerable<SubmittedDocument>)source, [], cancellation.Token);
        });

        Assert.True(source.ReceivedCancellation.CanBeCanceled);
        Assert.True(source.ReceivedCancellation.IsCancellationRequested);
        Assert.Equal(1, source.DisposeCalls);
        Assert.False(index.RequiresRebuild);
        Assert.Equal(identity, index.GetIndexIdentity());
        using var snapshot = index.OpenSnapshot();
        AssertOldDocument(snapshot);
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("dispose")]
    [InlineData("metadata")]
    public async Task CallerFailureDoesNotClearExistingRecoveryRequirement(string callback)
    {
        using var fixture = new CorpusFixture();
        using (var original = new SiftIndex(fixture.CorpusPath))
            await original.Build(new InMemoryDocumentSource([("old.txt", "alpha")]));
        File.WriteAllBytes(Path.Combine(fixture.CorpusPath, "lock", "rebuild.required"), [1]);
        using var index = new SiftIndex(fixture.CorpusPath);
        Assert.True(index.RequiresRebuild);
        var failure = new InvalidDataException("Caller recovery failure.");
        var source = new FailingSource(callback, failure);
        var caught = await Record.ExceptionAsync(async () =>
        {
            if (callback == "metadata")
                await index.Build(new InMemoryDocumentSource([("new.txt", "beta")]),
                    new BuildOptions { CommitMetadataProvider = _ => ThrowMetadata(failure) });
            else if (callback == "sync") await index.Build((IDocumentSource)source);
            else await index.Build((IAsyncEnumerable<SubmittedDocument>)source);
        });

        Assert.Same(failure, caught);
        Assert.True(index.RequiresRebuild);
        Assert.Throws<InvalidDataException>(() => index.OpenSnapshot());
        await index.Build(new InMemoryDocumentSource([("new.txt", "beta")]));
        Assert.False(index.RequiresRebuild);
    }

    private static IEnumerable<Exception> Failures() =>
    [
        new InvalidDataException("Caller data failure."), new FileNotFoundException("Caller file failure."),
        new DirectoryNotFoundException("Caller directory failure."), new EndOfStreamException("Caller stream failure."),
        new IOException("Caller I/O failure."), new InvalidOperationException("Caller operation failure."),
        new OperationCanceledException("Caller cancellation.")
    ];

    private static ReadOnlyMemory<byte> ThrowMetadata(Exception failure) => throw failure;

    private static void AssertOldDocument(IIndexSnapshot snapshot)
    {
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("old.txt"), out var bytes));
        Assert.Equal("alpha"u8.ToArray(), bytes.ToArray());
        Assert.False(snapshot.TryGetDocumentBytes(new DocumentKey("new.txt"), out _));
        Assert.Equal(1, snapshot.Search(new SearchQuery("", PatternMode.Literal, CaseMode.Sensitive),
            new(), new HitBuffer()).HitsReturned);
        Assert.Equal(1, snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new(), new HitBuffer()).HitsReturned);
    }

    private sealed class FailingSource(string callback, Exception failure, CancellationTokenSource? cancellation = null)
        : IDocumentSource, IAsyncEnumerable<SubmittedDocument>, IAsyncEnumerator<SubmittedDocument>
    {
        private int _reads;
        public int DisposeCalls { get; private set; }
        public CancellationToken ReceivedCancellation { get; private set; }
        public SubmittedDocument Current => callback == "current" && _reads == 3
            ? throw failure : new SubmittedDocument($"new-{_reads}.txt", "beta"u8.ToArray());

        public bool TryReadNext(out SubmittedDocument document)
        {
            if (++_reads == 3) throw failure;
            document = new SubmittedDocument($"new-{_reads}.txt", "beta"u8.ToArray());
            return true;
        }

        public IAsyncEnumerator<SubmittedDocument> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            ReceivedCancellation = cancellationToken;
            if (callback == "get-enumerator") throw failure;
            return this;
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            await Task.Yield();
            if (++_reads == 3)
            {
                if (callback == "move-next") throw failure;
                if (callback == "cancel")
                {
                    cancellation!.Cancel();
                    ReceivedCancellation.ThrowIfCancellationRequested();
                }
            }
            return _reads <= (callback == "current" ? 3 : 2);
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            await Task.Yield();
            if (callback == "dispose") throw failure;
        }
    }
}
