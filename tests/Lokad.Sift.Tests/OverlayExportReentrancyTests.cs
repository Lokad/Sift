namespace Lokad.Sift.Tests;

public sealed class OverlayExportReentrancyTests
{
    [Theory]
    [InlineData(false, "source")]
    [InlineData(true, "source")]
    [InlineData(false, "apply-metadata")]
    [InlineData(true, "apply-metadata")]
    [InlineData(false, "commit-metadata")]
    [InlineData(true, "commit-metadata")]
    public async Task ExportCallbacksRejectReentryAndPreserveState(bool disk, string callback)
    {
        foreach (var commit in new[] { false, true })
        foreach (var viaTask in new[] { false, true })
        foreach (var propagate in new[] { false, true })
        {
            using var fixture = new CorpusFixture();
            using var index = CreateIndex(disk, fixture.CorpusPath, "base");
            using var target = CreateIndex(disk, fixture.CorpusPath, "target");
            using var otherTarget = CreateIndex(disk, fixture.CorpusPath, "other");
            await index.Build(new InMemoryDocumentSource([("a.txt", "base")]));
            await target.Build(new InMemoryDocumentSource([]));
            await otherTarget.Build(new InMemoryDocumentSource([]));
            using var retained = index.OpenSnapshot();
            using var overlay = index.OpenOverlaySnapshot();
            await overlay.Update(new InMemoryDocumentSource([("a.txt", "alpha")]), []);
            var baseIdentity = index.GetIndexIdentity();
            var targetIdentity = target.GetIndexIdentity();
            var otherIdentity = otherTarget.GetIndexIdentity();
            Exception? failure = null;
            void Probe()
            {
                Assert.True(overlay.TryGetDocumentBytes(new DocumentKey("a.txt"), out var bytes));
                Assert.Equal("alpha"u8.ToArray(), bytes.ToArray());
                // Bound the old self-wait so a failing regression can unwind cleanly.
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    Task Export() => commit
                        ? overlay.CommitToBase(index, new UpdateOptions(), cancellation.Token)
                        : overlay.ApplyDeltaTo(otherTarget, new UpdateOptions(), cancellation.Token);
                    (viaTask ? Task.Run(Export) : Export()).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    failure = exception;
                    if (propagate) throw;
                }
            }
            var caught = await Record.ExceptionAsync(() => Task.Run(async () =>
            {
                var options = new UpdateOptions { CommitMetadataProvider = _ => { Probe(); return default; } };
                if (callback == "source")
                    await overlay.Update(new CallbackSource(Probe), []);
                else if (callback == "apply-metadata")
                    await overlay.ApplyDeltaTo(target, options, CancellationToken.None);
                else
                    await overlay.CommitToBase(index, options, CancellationToken.None);
            }));

            Assert.IsType<InvalidOperationException>(failure);
            if (propagate)
            {
                Assert.Same(failure, caught);
                Assert.Equal(baseIdentity, index.GetIndexIdentity());
                Assert.Equal(targetIdentity, target.GetIndexIdentity());
                Assert.False(overlay.TryGetDocumentBytes(new DocumentKey("b.txt"), out _));
            }
            else
                Assert.Null(caught);
            Assert.Equal(otherIdentity, otherTarget.GetIndexIdentity());
            Assert.False(index.RequiresRebuild || target.RequiresRebuild || otherTarget.RequiresRebuild);
            Assert.True(retained.TryGetDocumentBytes(new DocumentKey("a.txt"), out var oldBytes));
            Assert.Equal("base"u8.ToArray(), oldBytes.ToArray());
            await overlay.Update(new InMemoryDocumentSource([("recovery.txt", "ok")]), []);
            await overlay.ApplyDeltaTo(otherTarget);
            using var exported = otherTarget.OpenSnapshot();
            Assert.True(exported.TryGetDocumentBytes(new DocumentKey("recovery.txt"), out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectorsRejectExportsButCanReadAndExportOtherOverlays(bool visit)
    {
        using var index = SiftIndex.CreateInMemory();
        using var target = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha")]));
        await target.Build(new InMemoryDocumentSource([]));
        using var overlay = index.OpenOverlaySnapshot();
        using var other = index.OpenOverlaySnapshot();
        foreach (var commit in new[] { false, true })
        foreach (var viaTask in new[] { false, true })
        foreach (var cancel in new[] { false, true })
        {
            Exception? failure = null;
            void Probe()
            {
                using var cancellation = new CancellationTokenSource();
                if (cancel) cancellation.Cancel();
                try
                {
                    Task Export() => commit
                        ? overlay.CommitToBase(index, new UpdateOptions(), cancellation.Token)
                        : overlay.ApplyDeltaTo(target, new UpdateOptions(), cancellation.Token);
                    (viaTask ? Task.Run(Export) : Export()).GetAwaiter().GetResult();
                }
                catch (Exception exception) { failure = exception; }
                Assert.True(overlay.TryGetDocumentBytes(new DocumentKey("a.txt"), out _));
                other.ApplyDeltaTo(target).GetAwaiter().GetResult();
            }
            if (visit) overlay.VisitDocuments(new CallbackKeyCollector(Probe));
            else overlay.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new(), new CallbackHitCollector(Probe));
            Assert.IsType<InvalidOperationException>(failure);
        }
        await overlay.CommitToBase(index);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UnrelatedExportsSerializeAndWaitingCallersCanCancel(bool disk, bool commit)
    {
        using var fixture = new CorpusFixture();
        using var index = CreateIndex(disk, fixture.CorpusPath, "base");
        using var target = CreateIndex(disk, fixture.CorpusPath, "target");
        await index.Build(new InMemoryDocumentSource([("a.txt", "base")]));
        await target.Build(new InMemoryDocumentSource([]));
        using var overlay = index.OpenOverlaySnapshot();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var exportCancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = Task.Run(() => overlay.Update(new CallbackSource(() => { ready.SetResult(); release.Wait(); }), []));
        Task<UpdateStats>? export = null;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var options = new UpdateOptions { CommitMetadataProvider = _ => new byte[] { 7 } };
            export = Task.Run(() =>
            {
                started.SetResult();
                return commit ? overlay.CommitToBase(index, options, exportCancellation.Token)
                    : overlay.ApplyDeltaTo(target, options, exportCancellation.Token);
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var cancelled = overlay.ApplyDeltaTo(target, new UpdateOptions(), cancellation.Token);
            await Task.Delay(100);
            Assert.False(export.IsCompleted);
            Assert.False(cancelled.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            release.Set();
            await update.WaitAsync(TimeSpan.FromSeconds(10));
            await export.WaitAsync(TimeSpan.FromSeconds(10));
            using var result = (commit ? index : target).OpenSnapshot();
            Assert.True(result.TryGetDocumentBytes(new DocumentKey("b.txt"), out _));
            Assert.Equal(new byte[] { 7 }, result.CommitMetadata.ToArray());
            if (commit)
                await Assert.ThrowsAsync<StaleIndexRevisionException>(() => overlay.CommitToBase(index));
        }
        finally
        {
            release.Set();
            cancellation.Cancel();
            exportCancellation.Cancel();
            await update.WaitAsync(TimeSpan.FromSeconds(10));
            if (export is not null)
                try { await export.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
        }
    }

    private static SiftIndex CreateIndex(bool disk, string root, string name) =>
        disk ? new SiftIndex(Path.Combine(root, name)) : SiftIndex.CreateInMemory();

    private sealed class CallbackSource(Action callback) : IDocumentSource
    {
        private bool _read;
        public bool TryReadNext(out SubmittedDocument document)
        {
            if (_read) { document = default; return false; }
            callback();
            _read = true;
            document = new SubmittedDocument("b.txt", "beta"u8.ToArray());
            return true;
        }
    }

    private sealed class CallbackKeyCollector(Action callback) : IDocumentKeyCollector
    {
        public bool OnDocument(DocumentKey key) { callback(); return false; }
    }
}
