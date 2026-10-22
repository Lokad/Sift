namespace Lokad.Sift.Tests;

[Collection("ProcessMetrics")]
public sealed class ShutdownTests
{
    [Theory]
    [InlineData(false, false, "index")]
    [InlineData(false, true, "index")]
    [InlineData(true, false, "index")]
    [InlineData(true, true, "index")]
    [InlineData(false, false, "snapshot")]
    [InlineData(false, true, "snapshot")]
    [InlineData(true, false, "snapshot")]
    [InlineData(true, true, "snapshot")]
    [InlineData(false, false, "query")]
    [InlineData(false, true, "query")]
    [InlineData(true, false, "query")]
    [InlineData(true, true, "query")]
    public async Task DisposalDrainsMutationsAndReleasesOwnersDespiteCancellationCallbackFailure(
        bool disk, bool throwingCallback, string retainedOwner)
    {
        using var fixture = new CorpusFixture();
        using var matchEntered = new ManualResetEventSlim();
        using var matchRelease = new ManualResetEventSlim();
        var engine = new RegexContractTestEngine();
        var options = new SiftOptions { RegexEngine = engine, RetainIndexingWorkspace = true };
        using var index = disk ? new SiftIndex(fixture.CorpusPath, options) : SiftIndex.CreateInMemory(options);
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        var query = new SearchQuery("alpha[0-9]+", PatternMode.Regex, CaseMode.Sensitive);
        using var retainedLease = index.OpenSnapshot();
        IIndexSnapshot? retained = retainedLease;
        Assert.Equal(1, retained.Search(query, new(), new HitBuffer()).HitsReturned);
        var pattern = Assert.Single(engine.Patterns);
        if (retainedOwner == "index") { retained.Dispose(); retained = null; }
        Task<SearchStats>? search = null;
        if (retainedOwner == "query")
        {
            engine.BeforeMatch = () =>
            {
                matchEntered.Set();
                Assert.True(matchRelease.Wait(TimeSpan.FromSeconds(10)));
            };
            search = Task.Run(() => retained!.Search(query, new(), new HitBuffer()));
        }

        var callbackFailure = new InvalidOperationException("Synthetic source cancellation failure.");
        var source = new WaitingSource(throwingCallback ? callbackFailure : null);
        var mutation = index.Update((IAsyncEnumerable<SubmittedDocument>)source, []);
        Task<Exception?>? disposal = null;
        try
        {
            if (search is not null) Assert.True(matchEntered.Wait(TimeSpan.FromSeconds(5)));
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposal = Task.Run<Exception?>(() => Record.Exception(index.Dispose));
            await source.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Source disposal is deliberately unfinished, so index disposal must
            // still be draining it, even if its cancellation callback threw.
            Assert.NotSame(disposal, await Task.WhenAny(disposal, Task.Delay(100)));
            Assert.False(pattern.Disposed);
        }
        finally
        {
            source.DisposeRelease.TrySetResult();
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            matchRelease.Set();
            if (search is not null) await search.WaitAsync(TimeSpan.FromSeconds(5));
            _ = await Record.ExceptionAsync(() => mutation.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        var failure = await disposal!;
        if (throwingCallback)
            Assert.Contains(callbackFailure, Assert.IsType<AggregateException>(failure).Flatten().InnerExceptions);
        else
            Assert.Null(failure);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.Equal(1, source.DisposeCalls);
        Assert.False(index.RequiresRebuild);
        Assert.Null(Record.Exception(index.Dispose));
        Assert.Throws<ObjectDisposedException>(() => index.OpenSnapshot());
        if (retained is not null)
        {
            Assert.False(pattern.Disposed);
            engine.BeforeMatch = null;
            Assert.Equal(1, retained.Search(query, new(), new HitBuffer()).HitsReturned);
            retained.Dispose();
        }
        Assert.True(pattern.Disposed);
        Assert.False(engine.Disposed);
    }

    private sealed class WaitingSource(Exception? callbackFailure)
        : IAsyncEnumerable<SubmittedDocument>, IAsyncEnumerator<SubmittedDocument>
    {
        private CancellationToken _token;
        private CancellationTokenRegistration _registration;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCalls;
        public SubmittedDocument Current => throw new InvalidOperationException("No document was yielded.");

        public IAsyncEnumerator<SubmittedDocument> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _token = cancellationToken;
            return this;
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            var waiting = Task.Delay(Timeout.Infinite, _token);
            if (callbackFailure is not null)
                _registration = _token.Register(() => throw callbackFailure);
            Started.SetResult();
            await waiting.ConfigureAwait(false);
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            _registration.Dispose();
            DisposeStarted.SetResult();
            await DisposeRelease.Task.ConfigureAwait(false);
        }
    }
}
