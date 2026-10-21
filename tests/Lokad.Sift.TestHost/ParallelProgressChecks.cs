using Lokad.Sift;

internal static class ParallelProgressChecks
{
    public static async Task<int> Run()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { TargetSegmentDocumentCount = 1 });
        await index.Build(new Documents());
        using var snapshot = index.OpenSnapshot(new SearchOptions(2));
        var query = new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive);
        snapshot.Search(query, new(), new Hits(_ => true));

        return await Task.Run(() =>
        {
            ThreadPool.GetMinThreads(out var minimumWorkers, out var minimumIo);
            ThreadPool.GetMaxThreads(out var maximumWorkers, out var maximumIo);
            using var done = new ManualResetEventSlim();
            using var rescueCancellation = new CancellationTokenSource();
            var rescued = false;
            var rescueStarted = false;
            var rescue = new Thread(() =>
            {
                if (done.Wait(TimeSpan.FromSeconds(5))) return;
                rescued = true;
                rescueCancellation.Cancel();
                ThreadPool.SetMaxThreads(maximumWorkers, maximumIo);
            }) { IsBackground = true };
            try
            {
                ThreadPool.SetMinThreads(1, minimumIo);
                if (!ThreadPool.SetMaxThreads(1, maximumIo)) throw new InvalidOperationException("Cannot isolate pool.");
                rescue.Start();
                rescueStarted = true;
                var callerThread = Environment.CurrentManagedThreadId;
                var seen = new List<(string Path, int Start)>();
                var stats = snapshot.Search(query, new(), new Hits(hit =>
                {
                    if (Environment.CurrentManagedThreadId != callerThread) throw new Exception("Collector changed threads.");
                    seen.Add((snapshot.Materialize(hit, 0, 0).Path, hit.StartByte));
                    return true;
                }), rescueCancellation.Token);
                Require(stats.StopReason == SearchStopReason.None && stats.HitsReturned == 6,
                    $"Saturated-pool results: {stats.StopReason}, hits={stats.HitsReturned}, elapsed={stats.ElapsedMilliseconds}");
                Require(seen.SequenceEqual(new[] { ("0.txt", 0), ("0.txt", 7), ("1.txt", 0), ("1.txt", 7), ("2.txt", 0), ("2.txt", 7) }), "Hit order changed.");

                Require(snapshot.Search(query with { MaxHits = 1 }, new(), new Hits(_ => true), rescueCancellation.Token).StopReason == SearchStopReason.MaxHits, "Hit limit failed.");
                var documents = snapshot.Search(query with { MaxMatchedDocuments = 1 }, new(), new Hits(_ => true), rescueCancellation.Token);
                Require(documents.StopReason == SearchStopReason.MaxMatchedDocuments && documents.HitsReturned == 2, "Document limit failed.");
                var perDocument = snapshot.Search(query with { MaxHitsPerDocument = 1 }, new(), new Hits(_ => true), rescueCancellation.Token);
                Require(perDocument.StopReason == SearchStopReason.None && perDocument.HitsReturned == 3, "Per-document limit failed.");
                var stopped = snapshot.Search(query, new(), new Hits(_ => false), rescueCancellation.Token);
                Require(stopped.StopReason == SearchStopReason.CollectorStopped && stopped.HitsReturned == 1, "Collector stop failed.");
                var expired = snapshot.Search(query with { MaxElapsedMilliseconds = 1 }, new(), new Hits(_ => { Thread.Sleep(5); return true; }), rescueCancellation.Token);
                Require(expired.StopReason == SearchStopReason.MaxElapsedTime, "Deadline failed.");

                using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(rescueCancellation.Token);
                try
                {
                    snapshot.Search(query, new(), new Hits(_ => { callerCancellation.Cancel(); return true; }), callerCancellation.Token);
                    throw new Exception("Caller cancellation was ignored.");
                }
                catch (OperationCanceledException exception) when (exception.CancellationToken == callerCancellation.Token) { }
                var failure = new InvalidDataException("Collector failure.");
                try { snapshot.Search(query, new(), new Hits(_ => throw failure), rescueCancellation.Token); throw new Exception("Collector failure lost."); }
                catch (InvalidDataException exception) when (ReferenceEquals(exception, failure)) { }
                Require(!index.RequiresRebuild, "Collector failure poisoned index.");
                Console.WriteLine("Saturated-pool order, progress, limits, deadline, cancellation and collector identity passed.");
                return 0;
            }
            finally
            {
                done.Set();
                ThreadPool.SetMaxThreads(maximumWorkers, maximumIo);
                ThreadPool.SetMinThreads(minimumWorkers, minimumIo);
                if (rescueStarted) rescue.Join();
                if (rescued) Console.Error.WriteLine("Watchdog rescued a stalled search.");
            }
        });
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Hits(Func<RawHit, bool> callback) : IHitCollector { public bool OnHit(in RawHit hit) => callback(hit); }
    private sealed class Documents : IDocumentSource
    {
        private int next;
        public bool TryReadNext(out SubmittedDocument document)
        {
            if (next == 3) { document = default; return false; }
            document = new SubmittedDocument($"{next++}.txt", "needle needle"u8.ToArray());
            return true;
        }
    }
}
