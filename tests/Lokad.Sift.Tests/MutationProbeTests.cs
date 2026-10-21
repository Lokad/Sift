using System.Diagnostics;

namespace Lokad.Sift.Tests;

public sealed class MutationProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedCallersCanWaitForWriterLockProbes(bool disk)
    {
        using var fixture = new CorpusFixture();
        using var index = disk ? new SiftIndex(fixture.CorpusPath) : SiftIndex.CreateInMemory();
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new CountdownEvent(2);
        var mutation = index.Build(new BlockingSource(() => { ready.SetResult(); release.Wait(); }));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var storage = Task.Run(() => { started.Signal(); return index.GetStorageStats(); });
            var compact = Task.Run(() => { started.Signal(); return index.ShouldCompact(new CompactOptions()); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            await Task.Delay(100);
            Assert.False(storage.IsCompleted);
            Assert.False(compact.IsCompleted);
            release.Set();
            await Task.WhenAll(storage, (Task)compact).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await mutation.WaitAsync(TimeSpan.FromSeconds(5));
            started.Dispose();
        }
        Assert.False(index.RequiresRebuild);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CallbackProbesRejectReentrancyWithoutHangingTheHost(bool disk, bool cancel)
    {
        using var fixture = new CorpusFixture();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).AncestorsAndSelf()
            .First(directory => directory.Name is "Release" or "Debug").Name;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var host = Path.Combine(root, "tests", "Lokad.Sift.TestHost", "bin", configuration, "net10.0", "Lokad.Sift.TestHost.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { host, "mutation-probes", fixture.CorpusPath, disk ? "disk" : "memory", cancel ? "cancel" : "continue" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            // This child checks a full callback matrix with durable disk writes.
            // Allow hosted Windows I/O time while keeping deadlocks bounded.
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(process.ExitCode == 0, $"{await output}\n{await error}");
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail($"Mutation probe host did not finish within its deadline.\n{await output}\n{await error}");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private sealed class BlockingSource(Action callback) : IDocumentSource
    {
        private bool _read;
        public bool TryReadNext(out SubmittedDocument document)
        {
            if (_read) { document = default; return false; }
            callback();
            _read = true;
            document = new SubmittedDocument("a.txt", "alpha"u8.ToArray());
            return true;
        }
    }
}
