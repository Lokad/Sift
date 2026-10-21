using Lokad.Sift;

internal static class MutationProbeChecks
{
    public static async Task Run(string root, bool disk, bool cancel)
    {
        using var otherIndex = SiftIndex.CreateInMemory();
        foreach (var build in new[] { false, true })
        foreach (var stage in new[] { "source", "metadata", "get-enumerator", "move-next", "current", "dispose" })
        {
            using var index = disk ? new SiftIndex(Path.Combine(root, Guid.NewGuid().ToString("N"))) : SiftIndex.CreateInMemory();
            await index.Build((IDocumentSource)new ProbeDocuments("none", static () => { }, "alpha"));
            using var retained = index.OpenSnapshot();
            using var cancellation = new CancellationTokenSource();
            var calls = 0;
            void CheckProbes()
            {
                Console.WriteLine($"{(disk ? "disk" : "memory")}/{(build ? "build" : "update")}/{stage}/{(cancel ? "cancel" : "continue")}");
                calls++;
                foreach (Action probe in new Action[] { () => index.GetStorageStats(), () => index.ShouldCompact(new CompactOptions()) })
                {
                    MustReject(probe);
                    MustReject(() => Task.Run(probe).GetAwaiter().GetResult());
                }
                _ = index.GetMaintenanceStats();
                _ = index.GetIndexState();
                Require(index.GetWriteActivityStats().IsWriteBusy, "Mutation activity was lost.");
                _ = otherIndex.GetStorageStats();
                _ = otherIndex.ShouldCompact(new CompactOptions());
                if (cancel) cancellation.Cancel();
            }

            var source = new ProbeDocuments(stage, CheckProbes, "beta");
            ReadOnlyMemory<byte> Metadata() { if (stage == "metadata") CheckProbes(); return default; }
            Task mutation;
            if (stage is "source" or "metadata")
                mutation = build ? index.Build((IDocumentSource)source, new BuildOptions { CommitMetadataProvider = _ => Metadata() }, cancellation.Token)
                    : index.Update((IDocumentSource)source, [], new UpdateOptions { CommitMetadataProvider = _ => Metadata() }, cancellation.Token);
            else
                mutation = build ? index.Build((IAsyncEnumerable<SubmittedDocument>)source, cancellation.Token)
                    : index.Update((IAsyncEnumerable<SubmittedDocument>)source, [], cancellation.Token);

            try { await mutation; }
            catch (OperationCanceledException) when (cancel && cancellation.IsCancellationRequested) { }
            Require(calls == 1, "The requested callback was not exercised exactly once.");
            Require(!index.RequiresRebuild, "Rejected probes poisoned the index.");
            _ = index.GetStorageStats();
            _ = index.ShouldCompact(new CompactOptions());
            Require(retained.TryGetDocumentBytes(new DocumentKey("a.txt"), out var old) && old.Span.SequenceEqual("alpha"u8),
                "Retained snapshot changed.");
            await index.Update((IDocumentSource)new ProbeDocuments("none", static () => { }, "gamma"), []);
            using var current = index.OpenSnapshot();
            Require(current.TryGetDocumentBytes(new DocumentKey("a.txt"), out var updated) && updated.Span.SequenceEqual("gamma"u8),
                "Subsequent mutation failed.");
            Console.WriteLine($"Completed {(build ? "build" : "update")}/{stage}.");
        }
    }

    private static void MustReject(Action probe)
    {
        try { probe(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("A callback writer-lock probe was not rejected.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ProbeDocuments(string stage, Action callback, string content)
        : IDocumentSource, IAsyncEnumerable<SubmittedDocument>, IAsyncEnumerator<SubmittedDocument>
    {
        private bool _read;
        public SubmittedDocument Current
        {
            get { if (stage == "current") callback(); return new SubmittedDocument("a.txt", System.Text.Encoding.UTF8.GetBytes(content)); }
        }
        public bool TryReadNext(out SubmittedDocument document)
        {
            if (_read) { document = default; return false; }
            if (stage == "source") callback();
            _read = true;
            document = new SubmittedDocument("a.txt", System.Text.Encoding.UTF8.GetBytes(content));
            return true;
        }
        public IAsyncEnumerator<SubmittedDocument> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (stage == "get-enumerator") callback();
            return this;
        }
        public async ValueTask<bool> MoveNextAsync()
        {
            await Task.Yield();
            if (_read) return false;
            if (stage == "move-next") callback();
            _read = true;
            return true;
        }
        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            if (stage == "dispose") callback();
        }
    }
}
