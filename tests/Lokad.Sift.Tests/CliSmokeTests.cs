using System.Diagnostics;
using System.Text;
using Lokad.Sift;

namespace Lokad.Sift.Tests;

[Collection("BenchmarkSmoke")]
public sealed class CliSmokeTests
{
    [Fact]
    public async Task ContextIsPrintedPerHitAndRespectsTruncation()
    {
        var before = new string('é', 150000);
        using var fixture = new CliCorpusFixture(("a.txt", before + "\nneedle needle\nAFTER\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var result = RunCli(["search", fixture.CorpusPath, "needle", "--literal", "--before=1", "--after=1"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.StandardOutput.Split("a.txt-1- ").Length - 1);
        Assert.Contains("a.txt:2:1: needle", result.StandardOutput);
        Assert.Contains("a.txt:2:8: needle", result.StandardOutput);
        Assert.Equal(2, result.StandardOutput.Split("[context truncated]").Length - 1);
        Assert.DoesNotContain("AFTER", result.StandardOutput);
        Assert.DoesNotContain('�', result.StandardOutput);
        Assert.True(result.StandardOutput.Length < before.Length * 2);
    }

    [Theory]
    [InlineData("search", "literal")]
    [InlineData("search", "--LITERAL")]
    [InlineData("explain", "regex")]
    public async Task ValidAliasesAndNumericLimitsRemainAccepted(string command, string mode)
    {
        using var fixture = new CliCorpusFixture(("a.txt", "needle needle"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var result = RunCli([command, fixture.CorpusPath, "needle", mode, "--before=0", "--after=2147483647", "--max-hits=1", "--max-docs=1", "--max-bytes=9223372036854775807"]);
        Assert.Equal(0, result.ExitCode);
        if (command == "search") Assert.Contains("hits=1", result.StandardOutput);
    }

    [Theory]
    [InlineData("BEFORE\nneedle\nAFTER\n", "needle", "a.txt-1- BEFORE", "a.txt:2:1: needle", "a.txt-3- AFTER")]
    [InlineData("avant é\r\nneedle\r\naprès é\r\n", "needle", "a.txt-1- avant é", "a.txt:2:1: needle", "a.txt-3- après é")]
    [InlineData("needle\nAFTER", "needle", null, "a.txt:1:1: needle", "a.txt-2- AFTER")]
    [InlineData("BEFORE\nneedle", "needle", "a.txt-1- BEFORE", "a.txt:2:1: needle", null)]
    [InlineData("BEFORE\nalpha\nbeta\nAFTER", "alpha\nbeta", "a.txt-1- BEFORE", "a.txt:2:1: alpha", "a.txt-4- AFTER")]
    public async Task ContextOptionsPrintMaterializedRows(string content, string pattern, string? before, string match, string? after)
    {
        using var fixture = new CliCorpusFixture(("a.txt", content));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var result = RunCli(["search", fixture.CorpusPath, pattern, "--literal", "--before=1", "--after=1"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(match, result.StandardOutput);
        if (before is not null) Assert.Contains(before, result.StandardOutput);
        if (after is not null) Assert.Contains(after, result.StandardOutput);
    }

    public static IEnumerable<object[]> InvalidSearchOptions()
    {
        foreach (var command in new[] { "search", "explain" })
        foreach (var option in new[] { "--max-hitz=1", "extra", "--before=-1", "--after=-1", "--before=no", "--after=2147483648", "--max-hits=0", "--max-docs=-1", "--max-bytes=0", "--max-bytes=9223372036854775808" })
            yield return [command, option];
    }

    [Theory]
    [MemberData(nameof(InvalidSearchOptions))]
    public void InvalidOptionsFailBeforeOpeningStorage(string command, string option)
    {
        using var fixture = new CliCorpusFixture();
        var result = RunCli([command, fixture.CorpusPath, "no-hit", option]);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(option.Split('=')[0], result.StandardError);
        Assert.DoesNotContain("Unhandled exception", result.StandardError);
        Assert.False(Directory.Exists(fixture.CorpusPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryCommandsExcludeOnlyTheCorpusSubtree(bool nested)
    {
        using var fixture = new CliCorpusFixture();
        var input = fixture.InputPath;
        Directory.CreateDirectory(Path.Combine(input, ".sift-neighbor"));
        File.WriteAllText(Path.Combine(input, "a.txt"), "needle one");
        File.WriteAllText(Path.Combine(input, ".sift-neighbor", "b.txt"), "needle two");
        var corpus = nested ? Path.Combine(input, ".sift") : fixture.CorpusPath;
        var corpusArgument = Path.Combine(corpus, "..", Path.GetFileName(corpus)) + Path.DirectorySeparatorChar;
        if (nested && OperatingSystem.IsWindows()) corpusArgument = corpusArgument.ToUpperInvariant();

        // Omit the input argument to cover the CLI's current-directory default.
        var build = RunCli(["build", corpusArgument], input);
        Assert.True(build.ExitCode == 0, build.StandardError);
        Assert.Contains("submitted=2 indexed=2 rejected=0", build.StandardOutput);
        var update = RunCli(["update", corpusArgument, input]);
        Assert.True(update.ExitCode == 0, update.StandardError);
        Assert.Contains("submittedUpserts=2", update.StandardOutput);
        Assert.Contains("skippedUpserts=2 rejectedUpserts=0", update.StandardOutput);
        var search = RunCli(["search", corpus, "needle", "--literal"]);
        Assert.Equal(0, search.ExitCode);
        Assert.Contains("hits=2", search.StandardOutput);
        Assert.Contains(".sift-neighbor/b.txt", search.StandardOutput);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("update")]
    public void DirectoryCommandsRejectEqualInputAndCorpusRoots(string command)
    {
        using var fixture = new CliCorpusFixture();
        Directory.CreateDirectory(fixture.InputPath);
        var path = Path.Combine(fixture.InputPath, "a.txt");
        File.WriteAllText(path, "original");

        var result = RunCli([command, fixture.InputPath, fixture.InputPath + Path.DirectorySeparatorChar]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("The corpus directory must differ from the input directory.", result.StandardError);
        Assert.Equal("original", File.ReadAllText(path));
        Assert.False(File.Exists(Path.Combine(fixture.InputPath, "manifest.bin")));
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("invalid-utf8")]
    [InlineData("oversized")]
    public async Task DirectoryUpdateReportsRejectedFilesAndRemovesStaleText(string rejection)
    {
        using var fixture = new CliCorpusFixture();
        Directory.CreateDirectory(fixture.InputPath);
        var changed = Path.Combine(fixture.InputPath, "changed.txt");
        File.WriteAllText(changed, "stale needle");
        File.WriteAllText(Path.Combine(fixture.InputPath, "kept.txt"), "kept needle");
        File.WriteAllBytes(Path.Combine(fixture.InputPath, "image.bin"), [0, 1, 2, 3]);
        var build = RunCli(["build", fixture.CorpusPath, fixture.InputPath]);
        Assert.True(build.ExitCode == 0, build.StandardError);
        Assert.Contains("indexed=2 rejected=1", build.StandardOutput);
        using var index = new SiftIndex(fixture.CorpusPath);
        using var retained = index.OpenSnapshot();

        var unchanged = RunCli(["update", fixture.CorpusPath, fixture.InputPath]);
        Assert.True(unchanged.ExitCode == 0, unchanged.StandardError);
        Assert.Contains("skippedUpserts=2 rejectedUpserts=1", unchanged.StandardOutput);
        if (rejection == "oversized")
        {
            using var file = File.Create(changed);
            file.SetLength(new SiftOptions().MaxDocumentBytes + 1L);
        }
        else File.WriteAllBytes(changed, rejection == "binary" ? [0, 1, 2, 3] : [0xFF, 0xFE]);

        var update = RunCli(["update", fixture.CorpusPath, fixture.InputPath]);
        Assert.True(update.ExitCode == 0, update.StandardError);
        Assert.Contains("rejectedUpserts=2 deletions=1", update.StandardOutput);
        Assert.True(retained.TryGetDocumentBytes(new DocumentKey("changed.txt"), out var oldContent));
        Assert.Equal("stale needle", Encoding.UTF8.GetString(oldContent.Span));
        using var current = index.OpenSnapshot();
        Assert.False(current.TryGetDocumentBytes(new DocumentKey("changed.txt"), out _));
        Assert.False(current.TryGetDocumentBytes(new DocumentKey("image.bin"), out _));
        Assert.True(current.TryGetDocumentBytes(new DocumentKey("kept.txt"), out _));
        File.Delete(Path.Combine(fixture.InputPath, "kept.txt"));
        var deletion = RunCli(["update", fixture.CorpusPath, fixture.InputPath, "kept.txt"]);
        Assert.True(deletion.ExitCode == 0, deletion.StandardError);
        Assert.Contains("deletions=1", deletion.StandardOutput);
        using var deleted = index.OpenSnapshot();
        Assert.False(deleted.TryGetDocumentBytes(new DocumentKey("kept.txt"), out _));
        Assert.True(current.TryGetDocumentBytes(new DocumentKey("kept.txt"), out _));
        await index.Validate();
    }

    [Fact]
    public async Task VerifyReportsCorpusIntegrityCounts()
    {
        using var fixture = new CliCorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("src/b.txt", "beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var result = RunCli(["verify", fixture.CorpusPath]);

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

        var explain = RunCli(["explain", fixture.CorpusPath, "beta", "--literal", "--path-prefix=src/"]);
        Assert.Equal(0, explain.ExitCode);
        Assert.Contains("pattern=Literal", explain.StandardOutput);
        Assert.Contains("candidate=DirectScan", explain.StandardOutput);
        Assert.Contains("combination=None", explain.StandardOutput);
        Assert.Contains("regexTimeout=StopSearch", explain.StandardOutput);
        Assert.Contains("pathPrefix=True", explain.StandardOutput);
        Assert.Contains("literalVerifier=True", explain.StandardOutput);

        var stats = RunCli(["stats", fixture.CorpusPath]);
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

        var result = RunCli(["search", fixture.CorpusPath, "beta", "--literal"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("src/a.txt:1:7: beta", result.StandardOutput);
        Assert.Contains("hits=1", result.StandardOutput);
    }

    private static CliResult RunCli(string[] arguments, string? workingDirectory = null)
    {
        var repoRoot = GetRepositoryRoot();
        var cliAssembly = GetCliAssemblyPath(repoRoot);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory ?? repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(cliAssembly);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start CLI process.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, standardOutput, standardError);
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private static string GetCliAssemblyPath(string repoRoot)
    {
        var configuration = GetBuildConfiguration();
        var cliAssembly = Path.Combine(
            repoRoot,
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
        public string InputPath => Path.Combine(_root, "input");

        public IDocumentSource CreateSource() => new InMemoryDocumentSource(_documents);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

}

