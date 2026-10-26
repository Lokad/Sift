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

    [Fact]
    public void CachedIndexRejectsAnotherSourceUntilExplicitRefresh()
    {
        using var fixture = new BenchmarkFixture();
        var first = fixture.CreateCorpus("first", "needle");
        var second = fixture.CreateCorpus("second", "different");
        var initial = fixture.Run(first, "--use-index-cache");
        Assert.Equal(0, initial.ExitCode);
        Assert.Equal(1, fixture.ReadReport().GetProperty("corpora")[0].GetProperty("queries")[0].GetProperty("hitsReturned").GetInt32());

        var mismatch = fixture.Run(second, "--query-only");
        Assert.NotEqual(0, mismatch.ExitCode);
        Assert.Contains("source provenance", mismatch.Error);
        Assert.Equal(0, fixture.Run(second, "--use-index-cache", "--refresh-index").ExitCode);
        Assert.Equal(0, fixture.Run(second, "--query-only").ExitCode);
        var report = fixture.ReadReport();
        Assert.Equal(0, report.GetProperty("corpora")[0].GetProperty("queries")[0].GetProperty("hitsReturned").GetInt32());
        Assert.Equal(64, report.GetProperty("benchmarkAssemblySha256").GetString()!.Length);
        Assert.Equal(64, report.GetProperty("corpora")[0].GetProperty("build").GetProperty("builderAssemblySha256").GetString()!.Length);
    }

    [Fact]
    public void CachedIndexRejectsChangedGitRevisionAndOldCachePolicy()
    {
        using var fixture = new BenchmarkFixture();
        var root = fixture.CreateCorpus("repository", "needle");
        var repository = Path.Combine(root, "sample");
        Assert.Equal(0, RunProcess("git", ["-C", repository, "init"]).ExitCode);
        Commit();
        Assert.Equal(0, fixture.Run(root, "--use-index-cache").ExitCode);
        File.WriteAllText(Path.Combine(repository, "one.txt"), "different");
        Commit();
        Assert.Contains("source provenance", fixture.Run(root, "--query-only").Error);
        Assert.Equal(0, fixture.Run(root, "--use-index-cache", "--refresh-index").ExitCode);

        var metadataPath = Path.Combine(fixture.Root, "cache", "sample-10", "bench-index.json");
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(metadataPath))!;
        metadata["cacheVersion"] = 0;
        File.WriteAllText(metadataPath, metadata.ToJsonString());
        Assert.Contains("source provenance", fixture.Run(root, "--query-only").Error);

        void Commit()
        {
            Assert.Equal(0, RunProcess("git", ["-C", repository, "add", "one.txt"]).ExitCode);
            Assert.Equal(0, RunProcess("git", ["-C", repository, "-c", "user.name=Sift test", "-c", "user.email=test@example.invalid", "commit", "-m", "Synthetic fixture"]).ExitCode);
        }
    }

    [Fact]
    public void BenchmarkInputSkipsDirectoryCyclesExclusionsAndOversizedBodies()
    {
        using var fixture = new BenchmarkFixture();
        var root = fixture.CreateCorpus("input", "needle");
        var corpus = Path.Combine(root, "sample");
        var excluded = Path.Combine(corpus, "node_modules");
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(excluded, "ignored.txt"), "needle");
        var large = Path.Combine(corpus, "large.txt");
        File.WriteAllBytes(large, []);
        if (OperatingSystem.IsWindows()) Assert.Equal(0, RunProcess("fsutil", ["sparse", "setflag", large]).ExitCode);
        using (var stream = File.OpenWrite(large)) stream.SetLength(2147483648L);
        var link = Path.Combine(corpus, "cycle");
        CreateDirectoryLink(link, corpus);
        try
        {
            var run = fixture.Run(root);
            Assert.True(run.ExitCode == 0, run.Error);
            var result = fixture.ReadReport().GetProperty("corpora")[0];
            var build = result.GetProperty("build");
            Assert.Equal(2, build.GetProperty("submittedDocuments").GetInt32());
            Assert.Equal(1, build.GetProperty("indexedDocuments").GetInt32());
            Assert.Equal(1, build.GetProperty("rejectedDocuments").GetInt32());
            Assert.Equal(2147483654L, build.GetProperty("submittedBytes").GetInt64());
            Assert.Equal(1, result.GetProperty("queries")[0].GetProperty("hitsReturned").GetInt32());
            Assert.True(build.GetProperty("peakManagedBytes").GetInt64() > 0);
        }
        finally { Directory.Delete(link); }
        Assert.Equal(0, fixture.Run(root, "--max-files=1").ExitCode);
        Assert.Equal(1, fixture.ReadReport().GetProperty("corpora")[0].GetProperty("build").GetProperty("submittedDocuments").GetInt32());
    }

    [Fact]
    public void BenchmarkRejectsLinkedCorpusRoots()
    {
        using var fixture = new BenchmarkFixture();
        var source = fixture.CreateCorpus("source", "needle");
        var linkedRoot = Path.Combine(fixture.Root, "linked");
        Directory.CreateDirectory(linkedRoot);
        var link = Path.Combine(linkedRoot, "sample");
        CreateDirectoryLink(link, Path.Combine(source, "sample"));
        try { Assert.Contains("must not be a symbolic link or junction", fixture.Run(linkedRoot).Error); }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void BenchmarkLauncherRejectsConcurrentRunsBeforeStartingAChild()
    {
        var repoRoot = GetRepositoryRoot();
        var lockDirectory = Path.Combine(repoRoot, "bench", "out");
        Directory.CreateDirectory(lockDirectory);
        using var held = new FileStream(Path.Combine(lockDirectory, ".run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var run = RunProcess("pwsh", ["-NoProfile", "-NonInteractive", "-File", Path.Combine(repoRoot, "bench.ps1"), "-SkipBuild"]);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("Another benchmark launch is active", run.Error);
    }

    [Fact]
    public void BenchmarkRunnerFindsItsDefaultQueryPack()
    {
        using var fixture = new BenchmarkFixture();
        var root = Path.Combine(fixture.Root, "empty");
        Directory.CreateDirectory(root);
        var run = RunProcess("dotnet", [GetBenchmarkAssemblyPath(GetRepositoryRoot()), "--real-corpus", "--real-corpus-root=" + root, "--json=" + fixture.ReportPath]);
        Assert.True(run.ExitCode == 0, run.Error);
        Assert.Equal("real-diverse", fixture.ReadReport().GetProperty("queryPackId").GetString());
    }

    [Fact]
    public void BenchmarkThresholdFailureReturnsNonzero()
    {
        var run = RunProcess("dotnet", [GetBenchmarkAssemblyPath(GetRepositoryRoot()), "--documents=10", "--warmup=0", "--iterations=1", "--assert-median-ms=literal-indexed:0"]);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("exceeded threshold", run.Error);
    }

    private static void CreateDirectoryLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        var escapedPath = path.Replace("'", "''");
        var escapedTarget = target.Replace("'", "''");
        Assert.Equal(0, RunProcess("pwsh", ["-NoProfile", "-NonInteractive", "-Command", $"New-Item -ItemType Junction -Path '{escapedPath}' -Target '{escapedTarget}' | Out-Null"]).ExitCode);
    }

    private static (int ExitCode, string Error) RunProcess(string command, string[] arguments)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Synthetic benchmark check exceeded one minute.");
        }
        return (process.ExitCode, error.GetAwaiter().GetResult() + output.GetAwaiter().GetResult());
    }

    private sealed class BenchmarkFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sift-benchmark-check-" + Guid.NewGuid().ToString("N"));
        public string ReportPath => Path.Combine(Root, "report.json");

        public BenchmarkFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "queries.json"), """{"id":"synthetic-check","queries":[{"id":"needle","pattern":"needle","caseMode":"Sensitive"}]}""");
        }

        public string CreateCorpus(string id, string text)
        {
            var directory = Path.Combine(Root, id, "sample");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "one.txt"), text);
            return Path.GetDirectoryName(directory)!;
        }

        public (int ExitCode, string Error) Run(string sourceRoot, params string[] extra) =>
            RunProcess("dotnet", [GetBenchmarkAssemblyPath(GetRepositoryRoot()), "--real-corpus", "--warmup=0", "--iterations=1", "--max-files=10",
                "--real-corpus-root=" + sourceRoot, "--index-cache-root=" + Path.Combine(Root, "cache"),
                "--real-query-pack-json=" + Path.Combine(Root, "queries.json"), "--json=" + ReportPath, .. extra]);

        public JsonElement ReadReport()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(ReportPath));
            return json.RootElement.Clone();
        }

        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(absolute));
            Assert.StartsWith("sift-benchmark-check-", Path.GetFileName(absolute));
            foreach (var file in Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(absolute, recursive: true);
        }
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



