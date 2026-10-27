using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lokad.Sift;

internal static class LifetimeChecks
{
    public static async Task<int> Run(string root, string scenario)
    {
        try
        {
            if (scenario == "active-drain") await CheckActiveDrain();
            else if (scenario == "queued-reclamation") await CheckReclamation(root);
            else if (scenario == "scope-retention") await CheckScopeRetention();
            else if (scenario == "guard-protection") await CheckGuardProtection();
            else if (scenario == "manifest-threads") await ManifestPublicationChecks.Run(root, separateProcess: false);
            else if (scenario == "manifest-process") await ManifestPublicationChecks.Run(root, separateProcess: true);
            else if (scenario == "manifest-windows") await ManifestPublicationChecks.CheckWindowsProperties(root);
            else await CheckQueued(scenario);
            Console.WriteLine("Lifetime check passed: " + scenario);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void LimitPool()
    {
        ThreadPool.GetMaxThreads(out _, out var io);
        Require(ThreadPool.SetMinThreads(1, 1) && ThreadPool.SetMaxThreads(1, io), "Could not bound the worker pool.");
    }

    private static async Task CheckQueued(string scenario)
    {
        LimitPool();
        await Task.Run(async () =>
        {
            foreach (var callerCancels in new[] { false, true })
            {
                using var index = SiftIndex.CreateInMemory();
                using var cancellation = new CancellationTokenSource();
                var source = new Documents("needle");
                Task[] pending;
                if (scenario == "queued-validation")
                {
                    if (callerCancels) cancellation.Cancel();
                    pending = Enumerable.Range(0, 32).Select(_ => (Task)index.Validate(cancellation.Token)).ToArray();
                }
                else
                {
                    pending = [scenario switch
                    {
                        "queued-build" => index.Build(source, cancellation.Token),
                        "queued-update" => index.Update(source, [], cancellation.Token),
                        "queued-compact" => index.Compact(new CompactOptions(), cancellation.Token),
                        _ => throw new InvalidOperationException("Unknown lifetime scenario: " + scenario)
                    }];
                    Require(index.GetWriteActivityStats().IsWriteBusy, "Queued mutation was not counted.");
                    if (callerCancels) cancellation.Cancel();
                }
                index.Dispose();
                foreach (var task in pending) await RequireCancelled(task);
                Require(source.Reads == 0, "Disposed queued work called its document source.");
                await DrainQueuedCallbacks();
            }
        });
    }

    private static async Task CheckReclamation(string root)
    {
        using var index = new SiftIndex(root, new SiftOptions { MinimumFreeBytesAfterMutation = 0 });
        await index.Build(new Documents("first"));
        var retained = index.OpenSnapshot();
        await index.Update(new Documents("replacement"), []);
        LimitPool();
        await Task.Run(async () =>
        {
            retained.Dispose();
            index.Dispose();
            // The late reclamation callback must not touch retired cancellation/events.
            await DrainQueuedCallbacks();
        });
    }

    private static async Task CheckActiveDrain()
    {
        using var started = new ManualResetEventSlim();
        using var cancelled = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposed = new ManualResetEventSlim();
        using var index = SiftIndex.CreateInMemory();
        var source = new ActiveSource(started, cancelled, release);
        var active = index.Build(source);
        Require(started.Wait(TimeSpan.FromSeconds(5)), "Active mutation did not start.");
        var queuedSource = new Documents("queued");
        var queued = index.Build(queuedSource);
        Exception? failure = null;
        var disposer = new Thread(() =>
        {
            try { index.Dispose(); }
            catch (Exception exception) { failure = exception; }
            finally { disposed.Set(); }
        }) { IsBackground = true };
        disposer.Start();
        try
        {
            Require(cancelled.Wait(TimeSpan.FromSeconds(5)), "Active source did not receive disposal cancellation.");
            Require(!disposed.Wait(TimeSpan.FromMilliseconds(100)), "Dispose abandoned an active callback.");
        }
        finally { release.Set(); }
        Require(disposed.Wait(TimeSpan.FromSeconds(5)), "Dispose did not drain active work.");
        disposer.Join();
        if (failure is not null) throw failure;
        await RequireCancelled(active);
        await RequireCancelled(queued);
        Require(source.Finished && queuedSource.Reads == 0, "Source cleanup or queued cancellation was lost.");
    }

    private static async Task RequireCancelled(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { Require(task.IsCanceled, "Task did not reach cancellation."); return; }
        throw new Exception("Queued operation completed instead of cancelling.");
    }

    private static async Task CheckScopeRetention()
    {
        var control = CreateDisposedOwners();
        RequireCollectible(control.Owners);
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var inherited = CreateDisposedOwners(release, started);
        try
        {
            Require(started.Wait(TimeSpan.FromSeconds(5)), "Inherited child did not start.");
            RequireCollectible(inherited.Owners);
            Require(!inherited.Child.IsCompleted, "Child did not outlive its disposed owners.");
        }
        finally { release.Set(); await inherited.Child; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference[] Owners, Task Child) CreateDisposedOwners(
        ManualResetEventSlim? release = null, ManualResetEventSlim? started = null)
    {
        using var index = SiftIndex.CreateInMemory();
        index.Build(new Documents("needle")).GetAwaiter().GetResult();
        using var snapshot = index.OpenSnapshot();
        var hits = new InheritingHits(release, started);
        snapshot.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new(), hits);
        var owners = new List<WeakReference> { new(index), new(snapshot) };
        Require(snapshot.TryGetDocumentBytes(new DocumentKey("a.txt"), out var bytes), "Document was not indexed.");
        if (MemoryMarshal.TryGetArray(bytes, out var array) && array.Array is not null)
            owners.Add(new WeakReference(array.Array));
        return (owners.ToArray(), hits.Child);
    }

    private static void RequireCollectible(WeakReference[] owners)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (owners.All(owner => !owner.IsAlive)) return;
            Thread.Sleep(10);
        }
        throw new Exception("An inactive inherited guard retained a disposed index, snapshot or managed document pack.");
    }

    private static async Task CheckGuardProtection()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new Documents("needle"));
        using var outer = index.OpenSnapshot();
        using var inner = index.OpenSnapshot();
        using var release = new ManualResetEventSlim();
        Task inherited = Task.CompletedTask;
        var callbacks = 0;
        var query = new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive);
        try
        {
            outer.Search(query, new(), new Hits(() =>
            {
                callbacks++;
                inner.Search(query, new(), new Hits(() =>
                {
                    callbacks++;
                    RequireRejected(inner.Dispose);
                    RequireRejected(outer.Dispose);
                    Task.Run(() =>
                    {
                        RequireRejected(inner.Dispose);
                        RequireRejected(outer.Dispose);
                    }).GetAwaiter().GetResult();
                    inherited = Task.Run(() => { release.Wait(); inner.Dispose(); outer.Dispose(); });
                    return false;
                }));
                RequireRejected(outer.Dispose);
                inner.Dispose();
                return false;
            }));
        }
        finally { release.Set(); await inherited; }
        Require(callbacks == 2, "Nested search callbacks did not run.");
        // Disposal remains idempotent, including after inherited inactive scopes exit.
        inner.Dispose();
        outer.Dispose();
    }

    private static void RequireRejected(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("An active callback was allowed to dispose its own snapshot.");
    }

    private static Task DrainQueuedCallbacks()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.UnsafeQueueUserWorkItem(static state => state.SetResult(), done, preferLocal: false);
        return done.Task;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Hits(Func<bool> onHit) : IHitCollector
    {
        public bool OnHit(in RawHit hit) => onHit();
    }

    private sealed class InheritingHits(ManualResetEventSlim? release, ManualResetEventSlim? started) : IHitCollector
    {
        public Task Child = Task.CompletedTask;
        public bool OnHit(in RawHit hit)
        {
            if (release is not null)
                Child = Task.Run(() => { started!.Set(); release.Wait(); });
            return false;
        }
    }

    private sealed class Documents(string text) : IDocumentSource
    {
        public int Reads;
        public bool TryReadNext(out SubmittedDocument document)
        {
            if (Reads++ != 0) { document = default; return false; }
            document = new SubmittedDocument("a.txt", System.Text.Encoding.UTF8.GetBytes(text));
            return true;
        }
    }

    private sealed class ActiveSource(ManualResetEventSlim started, ManualResetEventSlim cancelled, ManualResetEventSlim release)
        : IAsyncEnumerable<SubmittedDocument>
    {
        public bool Finished;
        public async IAsyncEnumerator<SubmittedDocument> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            try
            {
                started.Set();
                cancellationToken.WaitHandle.WaitOne();
                cancelled.Set();
                release.Wait();
                cancellationToken.ThrowIfCancellationRequested();
                yield break;
            }
            finally { Finished = true; }
        }
    }
}
