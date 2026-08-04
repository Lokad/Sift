using System.Diagnostics;
using System.Text;
using Lokad.Sift;

namespace Lokad.Sift.Tests;

[Collection("BenchmarkSmoke")]
public sealed class CliSmokeTests
{
    [Fact]
    public async Task VerifyReportsCorpusIntegrityCounts()
    {
        using var fixture = new CliCorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var result = RunCli($"verify \"{fixture.CorpusPath}\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("verifiedSegments=1", result.StandardOutput);
        Assert.Contains("verifiedDocuments=2", result.StandardOutput);
    }

    [Fact]
    public async Task ExplainAndStatsExposeExtendedFields()
    {
        using var fixture = new CliCorpusFixture(
            ("src/a.txt", "alpha beta\n"),
            ("tests/b.txt", "beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var explain = RunCli($"explain \"{fixture.CorpusPath}\" beta --literal --path-prefix=src/");
        Assert.Equal(0, explain.ExitCode);
        Assert.Contains("pattern=Literal", explain.StandardOutput);
        Assert.Contains("planFamily=LiteralDirectScan", explain.StandardOutput);
        Assert.Contains("timeout=Default", explain.StandardOutput);
        Assert.Contains("pathPrefix=True", explain.StandardOutput);
        Assert.Contains("literalVerifier=True", explain.StandardOutput);

        var stats = RunCli($"stats \"{fixture.CorpusPath}\"");
        Assert.Equal(0, stats.ExitCode);
        Assert.Contains("liveContentBytes=", stats.StandardOutput);
        Assert.Contains("physicalSegmentBytes=", stats.StandardOutput);
        Assert.Contains("retiredSegmentBytes=", stats.StandardOutput);
        Assert.Contains("regexCacheHitRate=", stats.StandardOutput);
    }

    [Fact]
    public async Task SearchMaterializesHitsInsideTheCollectorCallback()
    {
        using var fixture = new CliCorpusFixture(
            ("src/a.txt", "alpha beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var result = RunCli($"search \"{fixture.CorpusPath}\" beta --literal");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("src/a.txt:1:7: beta", result.StandardOutput);
        Assert.Contains("hits=1", result.StandardOutput);
    }

    private static CliResult RunCli(string arguments)
    {
        var repoRoot = GetRepositoryRoot();
        var cliAssembly = GetCliAssemblyPath(repoRoot);
        var startInfo = new ProcessStartInfo("dotnet", $"\"{cliAssembly}\" {arguments}")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start CLI process.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, standardOutput, standardError);
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    }

    private static string GetCliAssemblyPath(string repoRoot)
    {
        var configuration = GetBuildConfiguration();
        var cliAssembly = Path.Combine(
            repoRoot,
            "sift",
            "src",
            "Lokad.Sift.Cli",
            "bin",
            configuration,
            "net10.0",
            "Lokad.Sift.Cli.dll");

        Assert.True(File.Exists(cliAssembly), $"Expected CLI assembly at '{cliAssembly}'. Build the solution before running tests.");
        return cliAssembly;
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

    private readonly record struct CliResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class CliCorpusFixture : IDisposable
    {
        private readonly string _root;
        private readonly IReadOnlyList<(string Path, string Content)> _documents;

        public CliCorpusFixture(params (string Path, string Content)[] documents)
        {
            _root = Path.Combine(Path.GetTempPath(), "sift-cli-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            CorpusPath = Path.Combine(_root, "corpus");
            _documents = documents;
        }

        public string CorpusPath { get; }

        public IDocumentSource CreateSource() => new InMemoryDocumentSource(_documents);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class InMemoryDocumentSource : IDocumentSource
    {
        private readonly IReadOnlyList<(string Path, string Content)> _documents;
        private int _index;

        public InMemoryDocumentSource(IReadOnlyList<(string Path, string Content)> documents)
        {
            _documents = documents;
        }

        public bool TryReadNext([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SubmittedDocument? document)
        {
            if (_index >= _documents.Count)
            {
                document = default;
                return false;
            }

            var next = _documents[_index++];
            document = new SubmittedDocument(next.Path, Encoding.UTF8.GetBytes(next.Content));
            return true;
        }
    }
}




