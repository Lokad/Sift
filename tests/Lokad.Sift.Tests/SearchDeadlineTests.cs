using System.Diagnostics;

namespace Lokad.Sift.Tests;

public sealed class SearchDeadlineTests
{
    [Fact]
    public async Task ScansObserveDeadlinesWhenTimerCallbacksCannotRun()
    {
        // Thread-pool saturation belongs in a separate process, so it cannot
        // affect other xUnit tests or their cancellation/timeout infrastructure.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).AncestorsAndSelf()
            .First(directory => directory.Name is "Release" or "Debug").Name;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var host = Path.Combine(root, "tests", "Lokad.Sift.TestHost", "bin", configuration, "net10.0", "Lokad.Sift.TestHost.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(host);
        start.ArgumentList.Add("deadline-probes");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(process.ExitCode == 0, $"{await output}\n{await error}");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}
