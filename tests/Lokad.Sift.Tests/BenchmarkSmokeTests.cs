using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Lokad.Sift.Tests;

[Collection("BenchmarkSmoke")]
public sealed class BenchmarkSmokeTests
{
    [Fact]
    public async Task BenchmarkRunnerAcceptsJsonProfile()
    {
        var repoRoot = GetRepositoryRoot();
        var benchmarkAssembly = GetBenchmarkAssemblyPath(repoRoot);
        var profilePath = Path.Combine(repoRoot, "src", "Lokad.Sift.Benchmarks", "Assets", "ci-gate.json");
        var startInfo = new ProcessStartInfo(
            "dotnet",
            $"\"{benchmarkAssembly}\" --profile-json=\"{profilePath}\" --documents=1000 --warmup=1 --iterations=1")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start benchmark process.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode is 0 or 1, $"Unexpected benchmark exit code {process.ExitCode}. stderr: {standardError}");
        Assert.Contains("literal-indexed:", standardOutput);
        Assert.Contains("ascii-insensitive-literal:", standardOutput);
        Assert.Contains("parallel-literal:", standardOutput);
        Assert.Contains("regex:", standardOutput);
        Assert.Contains("mandatory-fragment-precheck:", standardOutput);
        Assert.Contains("regex-verifier-path:", standardOutput);
        Assert.DoesNotContain("Unhandled", standardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", standardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BenchmarkRunnerSupportsRealCorpusModeWithoutLocalRepos()
    {
        var repoRoot = GetRepositoryRoot();
        var benchmarkAssembly = GetBenchmarkAssemblyPath(repoRoot);
        var queryPackPath = Path.Combine(repoRoot, "src", "Lokad.Sift.Benchmarks", "Assets", "real-default.json");
        var emptyCorpusRoot = Path.Combine(Path.GetTempPath(), "sift-empty-corpora", Guid.NewGuid().ToString("N"));
        var jsonOutput = Path.Combine(Path.GetTempPath(), "sift-real-bench", Guid.NewGuid().ToString("N"), "result.json");
        Directory.CreateDirectory(emptyCorpusRoot);

        try
        {
            var startInfo = new ProcessStartInfo(
                "dotnet",
                $"\"{benchmarkAssembly}\" --real-corpus --real-corpus-root=\"{emptyCorpusRoot}\" --real-query-pack-json=\"{queryPackPath}\" --json=\"{jsonOutput}\" --warmup=1 --iterations=1 --max-files=100")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false
            };

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start benchmark process.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(jsonOutput));
            using var output = JsonDocument.Parse(File.ReadAllText(jsonOutput));
            Assert.Equal("real-corpus", output.RootElement.GetProperty("toolVersion").GetString());
            Assert.Equal("real-default", output.RootElement.GetProperty("queryPackId").GetString());
            Assert.Equal(100, output.RootElement.GetProperty("maxFiles").GetInt32());
            Assert.Equal(0, output.RootElement.GetProperty("corpora").GetArrayLength());
            Assert.DoesNotContain("Unhandled", standardError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Exception", standardError, StringComparison.OrdinalIgnoreCase);
            Assert.True(string.IsNullOrWhiteSpace(standardOutput) || !standardOutput.Contains("exceeded threshold", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(emptyCorpusRoot))
            {
                Directory.Delete(emptyCorpusRoot, recursive: true);
            }
        }
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private static string GetBenchmarkAssemblyPath(string repoRoot)
    {
        var configuration = GetBuildConfiguration();
        var benchmarkAssembly = Path.Combine(
            repoRoot,
            "src",
            "Lokad.Sift.Benchmarks",
            "bin",
            configuration,
            "net10.0",
            "Lokad.Sift.Benchmarks.dll");

        Assert.True(File.Exists(benchmarkAssembly), $"Expected benchmark assembly at '{benchmarkAssembly}'. Build the solution before running tests.");
        return benchmarkAssembly;
    }

    private static string GetBuildConfiguration()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (baseDirectory is not null)
        {
            if (string.Equals(baseDirectory.Name, "Debug", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(baseDirectory.Name, "Release", StringComparison.OrdinalIgnoreCase))
            {
                return baseDirectory.Name;
            }

            baseDirectory = baseDirectory.Parent;
        }

        throw new InvalidOperationException($"Unable to infer build configuration from '{AppContext.BaseDirectory}'.");
    }
}



