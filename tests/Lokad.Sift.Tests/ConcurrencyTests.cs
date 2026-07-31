using Lokad.Sift;

namespace Lokad.Sift.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task ConcurrentReadersCanSearchSameCorpus()
    {
        var documents = Enumerable.Range(0, 2000)
            .Select(static i => ($"src/{i:D5}.txt", $"alpha beta gamma {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            using var snapshot = index.OpenSnapshot();
            var collector = new HitBuffer();
            var stats = snapshot.Search(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);
            Assert.Equal(2000, stats.HitsReturned);
        }));

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ActivityStatsExposeRunningBuild()
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath);
        using var source = new BlockingDocumentSource("src/a.txt", "alpha\n");

        var buildTask = index.Build(source);
        await source.WaitUntilBlocked();

        var busy = index.GetActivityStats();
        Assert.True(busy.IsWriteBusy);
        Assert.Equal(SiftActivity.Build, busy.CurrentActivity);

        source.Release();
        await buildTask;

        var idle = index.GetActivityStats();
        Assert.False(idle.IsWriteBusy);
        Assert.Equal(SiftActivity.Idle, idle.CurrentActivity);
    }

    [Fact]
    public async Task WaitingWriterCanBeCancelledBeforeLockAcquisition()
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath);
        using var blockingSource = new BlockingDocumentSource("src/a.txt", "alpha\n");

        var buildTask = index.Build(blockingSource);
        await blockingSource.WaitUntilBlocked();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await index.Update(new InMemoryDocumentSource([("src/b.txt", "beta\n")]), [], cancellation.Token));

        blockingSource.Release();
        await buildTask;
    }

    [Fact]
    public async Task PreCancelledWriteDoesNotLeakActivity()
    {
        using var fixture = new CorpusFixture();
        using var index = new SiftIndex(fixture.CorpusPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await index.Build(
                new InMemoryDocumentSource([("src/a.txt", "alpha\n")]),
                cancellation.Token));

        var activity = index.GetActivityStats();
        Assert.False(activity.IsWriteBusy);
        Assert.Equal(SiftActivity.Idle, activity.CurrentActivity);
    }

    [Fact]
    public async Task DisposedIndexRejectsNewOperations()
    {
        using var fixture = new CorpusFixture();
        var index = new SiftIndex(fixture.CorpusPath);
        index.Dispose();

        Assert.Throws<ObjectDisposedException>(() => index.OpenSnapshot());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await index.Build(new InMemoryDocumentSource([])));
    }

    [Fact]
    public async Task DisposeCancelsAWriterWaitingForTheCorpusLock()
    {
        using var fixture = new CorpusFixture();
        var storage = new Lokad.Sift.Storage.CorpusStorage(fixture.CorpusPath);
        using var writerLock = storage.AcquireWriterLock();
        var index = new SiftIndex(fixture.CorpusPath);
        var build = index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha\n")]));

        var dispose = Task.Run(index.Dispose);

        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await build);
    }

    [Fact]
    public async Task DisposeWaitsForAnActiveDocumentSourceToReturn()
    {
        using var fixture = new CorpusFixture();
        var index = new SiftIndex(fixture.CorpusPath);
        using var source = new BlockingDocumentSource("src/a.txt", "alpha\n");
        var build = index.Build(source);
        await source.WaitUntilBlocked();

        var dispose = Task.Run(index.Dispose);
        await Task.Delay(100);
        Assert.False(dispose.IsCompleted);

        source.Release();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await build);
    }

    [Fact]
    public async Task ReaderSnapshotRemainsConsistentDuringUpdatePublication()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var oldSnapshot = index.OpenSnapshot();
        var writer = Task.Run(() => index.Update(new InMemoryDocumentSource([
            ("src/a.txt", "omega\n"),
            ("src/c.txt", "alpha\n")
        ]), []));

        var collector = new HitBuffer();
        var stats = oldSnapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);

        await writer;

        Assert.Equal(1, stats.HitsReturned);
        Assert.Equal("src/a.txt", oldSnapshot.Materialize(collector.Hits[0], 0, 0).Path);
    }

    [Fact]
    public async Task ReaderSnapshotRemainsConsistentDuringCompactionPublication()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(new InMemoryDocumentSource([("src/a.txt", "omega\n")]), [new DocumentKey("src/b.txt")]);

        using var oldSnapshot = index.OpenSnapshot();
        var compactor = Task.Run(async () => await index.Compact(new CompactOptions()));

        var collector = new HitBuffer();
        var stats = oldSnapshot.Search(new SearchQuery("omega", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);

        await compactor;

        Assert.Equal(1, stats.HitsReturned);
        Assert.Equal("src/a.txt", oldSnapshot.Materialize(collector.Hits[0], 0, 0).Path);
    }

    private sealed class BlockingDocumentSource : IDocumentSource, IDisposable
    {
        private readonly SubmittedDocument _document;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(false);
        private int _state;

        public BlockingDocumentSource(string path, string content)
        {
            _document = new SubmittedDocument(path, System.Text.Encoding.UTF8.GetBytes(content));
        }

        public bool TryReadNext(out SubmittedDocument document)
        {
            if (Interlocked.Exchange(ref _state, 1) == 0)
            {
                document = _document;
                _entered.TrySetResult();
                _release.Wait();
                return true;
            }

            document = default;
            return false;
        }

        public Task WaitUntilBlocked()
        {
            return _entered.Task;
        }

        public void Release()
        {
            _release.Set();
        }

        public void Dispose()
        {
            _release.Dispose();
        }
    }
}




