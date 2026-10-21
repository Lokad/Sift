using Lokad.Sift;

internal static class SearchDeadlineChecks
{
    public static async Task<int> Run()
    {
        const int contentLength = 16 * 1024 * 1024;
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            MaxDocumentBytes = contentLength,
            DirectScanScopeBytesThreshold = long.MaxValue
        });
        await index.Build(new Source(new string('a', contentLength)));
        using var snapshot = index.OpenSnapshot();
        SearchQuery[] queries = [
            new(new string('a', 2047) + 'b', PatternMode.Literal, CaseMode.Insensitive),
            new(new string('a', 1023) + "b.*", PatternMode.Regex, CaseMode.Insensitive)
        ];
        foreach (var query in queries) snapshot.Search(query, new(), new Hits());

        return await Task.Run(() =>
        {
            ThreadPool.GetMinThreads(out var minimumWorkers, out var minimumIo);
            ThreadPool.GetMaxThreads(out var maximumWorkers, out var maximumIo);
            try
            {
                ThreadPool.SetMinThreads(1, minimumIo);
                if (!ThreadPool.SetMaxThreads(1, maximumIo)) throw new InvalidOperationException("Cannot isolate thread pool.");
                foreach (var query in queries)
                {
                    var stats = snapshot.Search(query with { MaxElapsedMilliseconds = 1 }, new(), new Hits());
                    Console.WriteLine($"{query.PatternMode}: elapsed={stats.ElapsedMilliseconds}, stop={stats.StopReason}");
                    if (stats.HitsReturned != 0 || stats.StopReason != SearchStopReason.MaxElapsedTime &&
                        !(stats.StopReason == SearchStopReason.None && stats.ElapsedMilliseconds < 1)) return 1;
                    var control = snapshot.Search(query, new(), new Hits());
                    if (control.StopReason != SearchStopReason.None || control.HitsReturned != 0) return 2;
                }
                return 0;
            }
            finally
            {
                ThreadPool.SetMaxThreads(maximumWorkers, maximumIo);
                ThreadPool.SetMinThreads(minimumWorkers, minimumIo);
            }
        });
    }

    private sealed class Source(string content) : IDocumentSource
    {
        private bool _read;
        public bool TryReadNext(out SubmittedDocument document)
        {
            if (_read) { document = default; return false; }
            _read = true;
            document = new SubmittedDocument("a.txt", System.Text.Encoding.UTF8.GetBytes(content));
            return true;
        }
    }

    private sealed class Hits : IHitCollector { public bool OnHit(in RawHit hit) => true; }
}
