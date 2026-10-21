using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Lokad.Sift;

internal static class ParallelQueueDeadlineChecks
{
    public static async Task<int> Run()
    {
        using var engine = new Engine();
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine, TargetSegmentDocumentCount = 1 });
        await index.Build(new Documents());
        using var snapshot = index.OpenSnapshot(new SearchOptions(2));
        var query = new SearchQuery("n.edle", PatternMode.Regex, CaseMode.Sensitive);
        snapshot.Search(query, new(), new Hits());
        return await Task.Run(() =>
        {
            ThreadPool.GetMinThreads(out var minimumWorkers, out var minimumIo);
            ThreadPool.GetMaxThreads(out var maximumWorkers, out var maximumIo);
            using var done = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource();
            var rescue = new Thread(() =>
            {
                if (done.Wait(TimeSpan.FromSeconds(5))) return;
                cancellation.Cancel();
                ThreadPool.SetMaxThreads(maximumWorkers, maximumIo);
            }) { IsBackground = true };
            try
            {
                ThreadPool.SetMinThreads(2, minimumIo);
                if (!ThreadPool.SetMaxThreads(2, maximumIo)) throw new InvalidOperationException("Cannot isolate pool.");
                engine.CallerThread = Environment.CurrentManagedThreadId;
                engine.BlockWorkers = true;
                rescue.Start();
                var stats = snapshot.Search(query with { MaxElapsedMilliseconds = 1000 }, new(), new Hits(), cancellation.Token);
                Console.WriteLine($"worker entered={engine.WorkerEntered.IsSet}, elapsed={stats.ElapsedMilliseconds}, stop={stats.StopReason}");
                return !cancellation.IsCancellationRequested && engine.WorkerEntered.IsSet &&
                    stats.Utf8RegexInvocations > 0 && stats.StopReason == SearchStopReason.MaxElapsedTime ? 0 : 1;
            }
            finally
            {
                done.Set();
                ThreadPool.SetMaxThreads(maximumWorkers, maximumIo);
                ThreadPool.SetMinThreads(minimumWorkers, minimumIo);
                if ((rescue.ThreadState & ThreadState.Unstarted) == 0) rescue.Join();
            }
        });
    }

    private sealed class Engine : ISiftRegexEngine, IDisposable
    {
        public readonly ManualResetEventSlim WorkerEntered = new();
        public int CallerThread;
        public bool BlockWorkers;
        public bool TryCompile(string pattern, RegexOptions options, TimeSpan timeout, Action checkpoint, [NotNullWhen(true)] out ISiftCompiledRegex? compiled)
        {
            compiled = pattern == "n.edle" ? new Pattern(this) : null;
            return compiled is not null;
        }
        public void Dispose() => WorkerEntered.Dispose();
        private sealed class Pattern(Engine owner) : ISiftCompiledRegex
        {
            public SiftRegexInputDomain InputDomain => SiftRegexInputDomain.Ascii;
            public void EnumerateMatches(ReadOnlySpan<byte> input, ISiftRegexMatchSink sink, Action checkpoint)
            {
                if (owner.BlockWorkers)
                {
                    if (Environment.CurrentManagedThreadId != owner.CallerThread)
                    {
                        owner.WorkerEntered.Set();
                        while (true) { checkpoint(); Thread.Sleep(1); }
                    }
                    while (!owner.WorkerEntered.IsSet) { checkpoint(); Thread.Sleep(1); }
                }
                checkpoint();
                sink.OnMatch(0, 6);
            }
            public void Dispose() { }
        }
    }
    private sealed class Hits : IHitCollector { public bool OnHit(in RawHit hit) => true; }
    private sealed class Documents : IDocumentSource
    {
        private int next;
        public bool TryReadNext(out SubmittedDocument document)
        {
            document = new SubmittedDocument($"{next++}.txt", "needle"u8.ToArray());
            return next <= 2;
        }
    }
}
