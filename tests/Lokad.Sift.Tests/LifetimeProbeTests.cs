using System.Diagnostics;

namespace Lokad.Sift.Tests;

public sealed class LifetimeProbeTests
{
    [Theory]
    [InlineData("queued-validation")]
    [InlineData("queued-build")]
    [InlineData("queued-update")]
    [InlineData("queued-compact")]
    [InlineData("queued-reclamation")]
    [InlineData("active-drain")]
    [InlineData("scope-retention")]
    [InlineData("guard-protection")]
    [InlineData("manifest-threads")]
    [InlineData("manifest-process")]
    [InlineData("manifest-windows")]
    public async Task ProductLifetimesRemainSafeAcrossQueuedAndInheritedWork(string scenario)
    {
        using var fixture = new CorpusFixture();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).AncestorsAndSelf()
            .First(directory => directory.Name is "Release" or "Debug").Name;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var host = Path.Combine(root, "tests", "Lokad.Sift.TestHost", "bin", configuration, "net10.0", "Lokad.Sift.TestHost.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { host, "lifetime-checks", fixture.CorpusPath, scenario }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(process.ExitCode == 0, $"{await output}\n{await error}");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}
