using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Lokad.Sift;

internal static class RegexPackageCheck
{
    public static async Task Run(ISiftRegexEngine? engine)
    {
        Require(!typeof(SiftIndex).Assembly.GetReferencedAssemblies().Any(
            reference => reference.Name!.Contains("Utf8Regex", StringComparison.Ordinal)), "Core assembly references the producer.");
        Require(typeof(SiftIndex).Assembly.GetType("Lokad.Sift.Search.RegexExecutionContext")!
            .GetProperty("ActiveQueryUsers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) is null,
            "Internal verification hooks entered the shipped assembly.");
        const System.Reflection.BindingFlags inspectionFlags = System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        foreach (var typeName in new[] { "SearchQueryPreparer", "PathFilterMatcher" })
        {
            var type = typeof(SiftIndex).Assembly.GetType("Lokad.Sift.Search." + typeName)!;
            Require(type.GetProperty("CacheCount", inspectionFlags) is null &&
                type.GetProperty("CacheCharacters", inspectionFlags) is null,
                "Test-only cache inspection entered the shipped assembly.");
        }
        Require(typeof(SiftIndex).GetProperty("Storage", inspectionFlags) is null,
            "Test-only storage inspection entered the shipped assembly.");
        var overlayType = typeof(SiftIndex).Assembly.GetType("Lokad.Sift.Search.OverlaySnapshot")!;
        Require(overlayType.GetMethod("GetOverlayMaintenanceStats", inspectionFlags) is null &&
            overlayType.GetMethod("GetRetainedTombstoneCount", inspectionFlags) is null,
            "Test-only overlay inspection entered the shipped assembly.");
        Require(typeof(SiftOptions).GetProperty("MeasurePeakManagedMemory") is null &&
            typeof(BuildStats).GetProperty("PeakManagedBytes") is null &&
            typeof(CompactStats).GetProperty("PeakManagedBytes") is null,
            "Removed memory-profiler API entered the shipped assembly.");
        Require(!typeof(SiftIndex).Assembly.GetTypes().Any(type => type.Name.Contains("ManagedMemoryPeakMeasurement")),
            "Removed managed-memory sampler entered the shipped assembly.");
        Require(typeof(SiftIndex).Assembly.GetType("Lokad.Sift.Storage.PostingsVisitor") is null &&
            typeof(SiftIndex).Assembly.GetType("Lokad.Sift.Storage.MappedSegment")!
                .GetMethod("VisitPostings", inspectionFlags | System.Reflection.BindingFlags.Public) is null,
            "Benchmark-only postings inspection entered the shipped assembly.");
        var documents = Enumerable.Range(0, 300)
            .Select(i => ($"src/noise-{i}.txt", "omega noise\n"))
            .Concat([("src/a.txt", "alpha123\nblue\n"), ("docs/unicode.txt", "café\n")]).ToArray();
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            RegexEngine = engine,
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1
        });
        await index.Build(new Documents(documents));
        using var snapshot = index.OpenSnapshot();
        var hits = new Hits();
        var query = new SearchQuery("alp.a", PatternMode.Regex, CaseMode.Sensitive);
        var stats = snapshot.Search(query, new(), hits);
        var plan = snapshot.Explain(query, new());
        Require(stats.HitsReturned == 1 && plan.UsesIndex, "Indexed regex search failed.");
        var expectedReason = engine is null ? SiftRegexRouteReason.NoEngine :
            engine is PackageAsciiEngine ? SiftRegexRouteReason.MixedInputDomains : SiftRegexRouteReason.EngineDeclined;
        Require(plan.RegexRouteReason == expectedReason, "Incorrect engine route.");
        Require(stats.Utf8RegexInvocations == (engine is PackageAsciiEngine ? 1 : 0), "Incorrect native counter.");
        Require(snapshot.Materialize(hits.Values.Single(), 0, 0).MatchText == "alpha", "Incorrect byte-range projection.");
        hits.Values.Clear();
        Require(snapshot.Search(new SearchQuery("café", PatternMode.Literal, CaseMode.Sensitive), new(), hits).HitsReturned == 1,
            "Unicode literal search failed.");
        hits.Values.Clear();
        Require(snapshot.Search(new SearchQuery("CAFÉ", PatternMode.Regex, CaseMode.Insensitive), new(), hits).HitsReturned == 1,
            "Unicode regex search failed.");
        hits.Values.Clear();
        var classic = snapshot.Search(new SearchQuery("alpha(?=[0-9])", PatternMode.Regex, CaseMode.Sensitive), new(), hits);
        Require(classic.HitsReturned == 1 && classic.FallbackRegexInvocations == 1 && classic.NonBacktrackingRegexInvocations == 0,
            "Classic fallback search failed.");
        hits.Values.Clear();
        Require(snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathRegex = "src/a\\.txt$", IncludeGlobs = ["src/*.txt"] }, hits).HitsReturned == 1,
            "Path filtering failed.");
        hits.Values.Clear();
        Require(snapshot.Search(new SearchQuery("(?x:al pha)", PatternMode.Regex, CaseMode.Sensitive),
            new(), hits).HitsReturned == 1 && snapshot.Materialize(hits.Values.Single(), 0, 0).MatchText == "alpha",
            "Scoped whitespace regex changed matching semantics.");
        hits.Values.Clear();
        Require(snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathRegex = @"src/[]a]\.txt$" }, hits).HitsReturned == 1,
            "Leading literal class delimiter excluded a matching path.");
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new Documents([("src/a.txt", "alpha999\n")] ), []);
        Require(overlay.Search(query, new(), new Hits()).HitsReturned == 1, "Overlay engine inheritance failed.");
        Console.WriteLine($"Package consumer passed: {expectedReason}; core={typeof(SiftIndex).Assembly.Location}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Documents((string Path, string Text)[] items) : IDocumentSource
    {
        private int _position;
        public bool TryReadNext(out SubmittedDocument document)
        {
            document = default;
            if (_position == items.Length) return false;
            var item = items[_position++];
            document = new SubmittedDocument(item.Path, Encoding.UTF8.GetBytes(item.Text));
            return true;
        }
    }

    private sealed class Hits : IHitCollector
    {
        public List<RawHit> Values { get; } = [];
        public bool OnHit(in RawHit hit) { Values.Add(hit); return true; }
    }
}

// An independent byte matcher for one controlled package fixture. This verifies
// native injection against the shipped contract without requiring producer capabilities.
internal sealed class PackageAsciiEngine : ISiftRegexEngine
{
    public bool TryCompile(string pattern, RegexOptions options, TimeSpan matchTimeout,
        Action checkpoint, [NotNullWhen(true)] out ISiftCompiledRegex? compiledPattern)
    {
        checkpoint();
        compiledPattern = null;
        if (pattern != "alp.a" || options != RegexOptions.CultureInvariant) return false;
        compiledPattern = new Pattern();
        return true;
    }

    private sealed class Pattern : ISiftCompiledRegex
    {
        public SiftRegexInputDomain InputDomain => SiftRegexInputDomain.Ascii;
        public void EnumerateMatches(ReadOnlySpan<byte> input, ISiftRegexMatchSink sink, Action checkpoint)
        {
            for (var i = 0; i <= input.Length - 5; i++)
            {
                checkpoint();
                if (input.Slice(i, 3).SequenceEqual("alp"u8) && input[i + 3] != (byte)'\n' && input[i + 4] == (byte)'a')
                {
                    if (!sink.OnMatch(i, 5)) return;
                    i += 4;
                }
            }
        }
        public void Dispose() { }
    }
}
