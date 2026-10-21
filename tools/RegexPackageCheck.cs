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
            .Concat([("src/a.txt", "alpha123\nblue\n"), ("docs/unicode.txt", "café\n"),
                ("docs/repeated.txt", "aaaa")]).ToArray();
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
        hits.Values.Clear();
        snapshot.Search(new SearchQuery("aa", PatternMode.Regex, CaseMode.Sensitive), new(), hits);
        Require(hits.Values.Select(hit => (hit.StartByte, hit.EndByte)).SequenceEqual([(0, 2), (2, 4)]),
            "Optimized regex enumeration returned overlapping matches.");
        hits.Values.Clear();
        snapshot.Search(new SearchQuery("aa", PatternMode.Literal, CaseMode.Sensitive), new(), hits);
        Require(hits.Values.Select(hit => (hit.StartByte, hit.EndByte)).SequenceEqual([(0, 2), (1, 3), (2, 4)]),
            "Literal enumeration lost overlapping matches.");
        hits.Values.Clear();
        snapshot.Search(new SearchQuery("(?:)", PatternMode.Regex, CaseMode.Sensitive),
            new PathFilter { PathPrefix = "docs/repeated.txt" }, hits);
        Require(hits.Values.Select(hit => (hit.StartByte, hit.EndByte))
                .SequenceEqual(Enumerable.Range(0, 5).Select(offset => (offset, offset))),
            "Empty-group regex enumeration lost zero-length matches.");
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new Documents([("src/a.txt", "alpha999\n")] ), []);
        Require(overlay.Search(query, new(), new Hits()).HitsReturned == 1, "Overlay engine inheritance failed.");
        await VerifyUnicodeAndEmptyQueries(engine);
        await VerifyGlobFilters(engine);
        await VerifyUnicodePathFilters(engine);
        await VerifyContextAndDecodeDiagnostics(engine);
        await VerifyLiteralSurrogateProjection(engine);
        await VerifyLiteralWholeLineBoundaries(engine);
        await VerifyRegexCandidateResults(engine);
        await VerifyCollectorFailurePropagation(engine);
        await VerifyMutationCallbackFailures(engine);
        await VerifyMutationProbeReentrancy(engine);
        VerifyRegexValidation(snapshot);
        Console.WriteLine($"Package consumer passed: {expectedReason}; core={typeof(SiftIndex).Assembly.Location}");
    }

    private static async Task VerifyRegexCandidateResults(ISiftRegexEngine? engine)
    {
        foreach (var (pattern, text, expectedHits) in new[]
        {
            ("foo.bar", "fooébar", 1), ("foo..bar", "foo😀bar", 1),
            ("foo[^x]{1,2}bar", "fooéébar", 1), ("aa.*aa", "aaaaa", 1),
            ("aba.*aba", "abababa", 1), ("foo.bar", "foo\nbar", 0)
        })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
            await index.Build(new Documents([("a.txt", "stale")]));
            using var overlay = index.OpenOverlaySnapshot();
            await overlay.Update(new Documents([("a.txt", text)]), []);
            await index.Update(new Documents([("a.txt", text)]), []);
            using var snapshot = index.OpenSnapshot();
            foreach (var view in new IIndexSnapshot[] { snapshot, overlay })
            foreach (var caseMode in new[] { CaseMode.Sensitive, CaseMode.Insensitive })
            {
                var hits = new Hits();
                var stats = view.Search(new SearchQuery(pattern, PatternMode.Regex, caseMode), new(), hits);
                Require(stats.HitsReturned == expectedHits, "Candidate filtering lost a managed regex result.");
                if (expectedHits == 0) continue;
                var hit = hits.Values.Single();
                Require(hit.StartByte == 0 && hit.EndByte == Encoding.UTF8.GetByteCount(text) &&
                    view.Materialize(hit, 0, 0).MatchText == text,
                    "Regex result has incorrect UTF-8 bounds or materialized text.");
            }
        }
    }

    private static async Task VerifyMutationProbeReentrancy(ISiftRegexEngine? engine)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new Documents([("a.txt", "alpha")]));
        foreach (var build in new[] { false, true })
        foreach (var sourceCallback in new[] { false, true })
        foreach (var storage in new[] { false, true })
        {
            var before = index.GetIndexIdentity();
            Exception? failure = null;
            void Probe()
            {
                _ = index.GetMaintenanceStats();
                _ = index.GetIndexState();
                try
                {
                    if (storage) _ = index.GetStorageStats();
                    else _ = index.ShouldCompact(new CompactOptions());
                }
                catch (InvalidOperationException exception) { failure = exception; throw; }
            }
            var documents = new Documents([("b.txt", "beta")], beforeRead: sourceCallback ? Probe : null);
            Exception? caught = null;
            try
            {
                if (build)
                    await index.Build(documents, new BuildOptions { CommitMetadataProvider = _ => { if (!sourceCallback) Probe(); return default; } });
                else
                    await index.Update(documents, [], new UpdateOptions { CommitMetadataProvider = _ => { if (!sourceCallback) Probe(); return default; } });
            }
            catch (Exception exception) { caught = exception; }
            Require(failure is not null && ReferenceEquals(failure, caught) && !index.RequiresRebuild,
                "Reentrant probe failure was lost or poisoned the index.");
            Require(index.GetIndexIdentity() == before, "Rejected probe changed the committed state.");
            _ = index.GetStorageStats();
            _ = index.ShouldCompact(new CompactOptions());
        }
        await index.Update(new Documents([("b.txt", "beta")]), []);
    }

    private static async Task VerifyUnicodePathFilters(ISiftRegexEngine? engine)
    {
        var paths = new[] { "😀.txt", "�.txt", "prefix/😀.txt", "prefix/�.txt" };
        foreach (var route in new[] { "snapshot", "parallel", "overlay-base", "overlay-own" })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine, TargetSegmentDocumentCount = 1 });
            await index.Build(new Documents(paths.Select(path => (path, "needle")).ToArray()));
            using IIndexSnapshot snapshot = route.StartsWith("overlay", StringComparison.Ordinal)
                ? index.OpenOverlaySnapshot(new SearchOptions(2))
                : index.OpenSnapshot(new SearchOptions(route == "parallel" ? 2 : 1));
            if (route == "overlay-own")
                await ((IOverlaySnapshot)snapshot).Update(new Documents(paths.Select(path => (path, "updated needle")).ToArray()), []);

            foreach (var (original, expected, indexed) in new (PathFilter, string[], bool)[]
            {
                (new() { PathPrefix = "\ud83d", IncludeGlobs = ["*"] }, ["😀.txt"], false),
                (new() { IncludeGlobs = ["\ud83d*"] }, ["😀.txt"], false),
                (new() { IncludeGlobs = ["*\ude00.txt"] }, ["😀.txt"], false),
                (new() { IncludeGlobs = ["prefix/\ud83d*"] }, ["prefix/😀.txt"], false),
                (new() { IncludeGlobs = ["\ud83d.txt"] }, [], false),
                (new() { ExcludeGlobs = ["\ud83d*"] }, ["�.txt", "prefix/😀.txt", "prefix/�.txt"], false),
                (new() { PathRegex = "^prefix/", IncludeGlobs = ["prefix/\ud83d*"] }, ["prefix/😀.txt"], true),
                (new() { IncludeGlobs = ["prefix/😀*"] }, ["prefix/😀.txt"], true),
                (new() { IncludeGlobs = ["prefix/�*"] }, ["prefix/�.txt"], true)
            })
            foreach (var pattern in new[] { "", "needle" })
            foreach (var pathRegex in new[] { original.PathRegex, original.PathRegex ?? ".*" }.Distinct())
            {
                var filter = new PathFilter(original.PathPrefix, original.IncludeGlobs, original.ExcludeGlobs, pathRegex);
                var query = new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive);
                var hits = new Hits();
                snapshot.Search(query, filter, hits);
                Require(hits.Values.Select(hit => snapshot.Materialize(hit, 0, 0).Path).Order(StringComparer.Ordinal)
                    .SequenceEqual(expected.Order(StringComparer.Ordinal)), "UTF-16 path filter changed meaning across byte/string routes.");
                Require(snapshot.Explain(query, filter).UsesPathIndex == indexed,
                    "Malformed path fragment entered the index or a safe hint was lost.");
            }
        }
    }

    private static async Task VerifyMutationCallbackFailures(ISiftRegexEngine? engine)
    {
        foreach (var build in new[] { false, true })
        foreach (var callback in new[] { "sync", "async", "dispose", "metadata" })
        foreach (var failure in new Exception[] { new InvalidDataException("Caller data failure."),
            new FileNotFoundException("Caller file failure."), new DirectoryNotFoundException("Caller directory failure."),
            new EndOfStreamException("Caller stream failure.") })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine, TargetSegmentDocumentCount = 1 });
            await index.Build(new Documents([("old.txt", "alpha")]));
            var before = index.GetIndexIdentity();
            using var retained = index.OpenSnapshot();
            Exception? caught = null;
            try
            {
                if (callback == "metadata")
                {
                    if (build)
                        await index.Build(new Documents([("new.txt", "beta")]),
                            new BuildOptions { CommitMetadataProvider = _ => throw failure });
                    else
                        await index.Update(new Documents([("new.txt", "beta")]), [],
                            new UpdateOptions { CommitMetadataProvider = _ => throw failure });
                }
                else if (callback == "sync")
                {
                    if (build) await index.Build(new Documents([("new.txt", "beta"), ("second.txt", "gamma")], failure));
                    else await index.Update(new Documents([("new.txt", "beta"), ("second.txt", "gamma")], failure), []);
                }
                else
                {
                    if (build) await index.Build(FailingDocuments(failure, callback == "dispose"));
                    else await index.Update(FailingDocuments(failure, callback == "dispose"), []);
                }
            }
            catch (Exception exception) { caught = exception; }
            Require(ReferenceEquals(failure, caught) && !index.RequiresRebuild,
                "Caller mutation failure was replaced or classified as index corruption.");
            Require(index.GetIndexIdentity() == before && index.GetStorageStats().PhysicalSegmentCount == 1,
                "Failed mutation changed the committed manifest or leaked a partition.");
            using var fresh = index.OpenSnapshot();
            Require(fresh.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new(), new Hits()).HitsReturned == 1 &&
                retained.TryGetDocumentBytes(new DocumentKey("old.txt"), out _) &&
                !fresh.TryGetDocumentBytes(new DocumentKey("new.txt"), out _), "Failed mutation invalidated snapshot contents.");
            await index.Update(new Documents([("new.txt", "beta")]), []);
        }
    }

    private static async IAsyncEnumerable<SubmittedDocument> FailingDocuments(Exception failure, bool failOnDispose)
    {
        try
        {
            await Task.Yield();
            yield return new SubmittedDocument("new.txt", "beta"u8.ToArray());
            yield return new SubmittedDocument("second.txt", "gamma"u8.ToArray());
            if (!failOnDispose) throw failure;
        }
        finally
        {
            if (failOnDispose) throw failure;
        }
    }

    private static async Task VerifyCollectorFailurePropagation(ISiftRegexEngine? engine)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine, TargetSegmentDocumentCount = 1 });
        await index.Build(new Documents([("a.txt", "alpha"), ("b.txt", "alpha")]));
        foreach (var mode in new[] { "snapshot", "parallel", "overlay", "overlay-base" })
        {
            using IIndexSnapshot snapshot = mode.StartsWith("overlay", StringComparison.Ordinal) ? index.OpenOverlaySnapshot() :
                index.OpenSnapshot(new SearchOptions(mode == "parallel" ? 2 : 1));
            if (snapshot is IOverlaySnapshot overlay && mode == "overlay")
                await overlay.Update(new Documents([("c.txt", "alpha")]), []);
            foreach (var (pattern, patternMode) in new[] { ("alpha", PatternMode.Literal), ("alp.a", PatternMode.Regex) })
            foreach (var policy in new[] { RegexTimeoutPolicy.StopSearch, RegexTimeoutPolicy.Throw })
            {
                var query = new SearchQuery(pattern, patternMode, CaseMode.Sensitive) { RegexTimeoutPolicy = policy };
                var failure = new RegexMatchTimeoutException("Collector failure, unrelated to Sift verification.");
                var collector = new ThrowingCollector(failure);
                Exception? caught = null;
                try { snapshot.Search(query, new(), collector); }
                catch (Exception exception) { caught = exception; }
                Require(ReferenceEquals(failure, caught) && collector.Calls == 1,
                    "Search swallowed or replaced a collector exception.");
                Require(snapshot.Search(query, new(), new Hits()).HitsReturned == (mode == "overlay" ? 3 : 2),
                    "Collector failure poisoned subsequent searches.");
            }
        }
    }

    private static async Task VerifyLiteralWholeLineBoundaries(ISiftRegexEngine? engine)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new Documents([("a.txt", "stale"), ("b.txt", "stale")]));
        using var overlay = index.OpenOverlaySnapshot();
        var documents = new[] { ("a.txt", "π\nalpha\r\r\n"), ("b.txt", "π\ncafé\r\r") };
        await overlay.Update(new Documents(documents), []);
        await index.Update(new Documents(documents), []);
        using var snapshot = index.OpenSnapshot();
        foreach (var view in new IIndexSnapshot[] { snapshot, overlay })
        foreach (var stem in new[] { "alpha", "café" })
        foreach (var caseMode in new[] { CaseMode.Sensitive, CaseMode.Insensitive })
        {
            var pattern = caseMode == CaseMode.Sensitive ? stem : stem.ToUpperInvariant();
            var query = new SearchQuery(pattern, PatternMode.Literal, caseMode) { WholeLine = true };
            Require(view.Search(query, new(), new Hits()).HitsReturned == 0,
                "Whole-line literal discarded extra carriage returns.");
            var hits = new Hits();
            view.Search(new SearchQuery(pattern + "\r", PatternMode.Literal, caseMode) { WholeLine = true }, new(), hits);
            var hit = hits.Values.Single();
            Require(hit.StartByte == 3 && hit.EndByte == 4 + Encoding.UTF8.GetByteCount(stem) &&
                view.Materialize(hit, 0, 0).MatchText == stem + "\r",
                "Whole-line literal lost its CR body byte or changed UTF-8 projection.");
        }
    }

    private static async Task VerifyLiteralSurrogateProjection(ISiftRegexEngine? engine)
    {
        foreach (var favorIndex in new[] { false, true })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions
            {
                RegexEngine = engine,
                DirectScanScopeDocumentsThreshold = favorIndex ? 0 : 256,
                DirectScanScopeBytesThreshold = favorIndex ? 0 : 1024 * 1024,
                DirectScanCandidateDensityThreshold = 1.1
            });
            await index.Build(new Documents(new[] { ("a.txt", "😀x😀"), ("b.txt", "😀😀😀"),
                ("replacement.txt", "�") }.Concat(Enumerable.Range(0, 64)
                .Select(i => ($"noise/{i:D2}.txt", ""))).ToArray()));
            using var snapshot = index.OpenSnapshot();
            foreach (var (pattern, expected) in new (string, (int, int)[])[]
            {
                ("\uD83D", [(0, 0), (5, 5), (0, 0), (4, 4), (8, 8)]),
                ("\uDE00", [(0, 4), (5, 9), (0, 4), (4, 8), (8, 12)]),
                ("\uDE00\uD83D", [(0, 4), (4, 8)])
            })
            foreach (var caseMode in new[] { CaseMode.Sensitive, CaseMode.Insensitive, CaseMode.Smart })
            {
                var query = new SearchQuery(pattern, PatternMode.Literal, caseMode);
                var hits = new Hits();
                var stats = snapshot.Search(query, new(), hits);
                var plan = snapshot.Explain(query, new());
                Require(hits.Values.Select(hit => (hit.StartByte, hit.EndByte)).SequenceEqual(expected),
                    "Literal surrogate matching lost overlaps or split a UTF-8 scalar.");
                Require(!plan.UsesIndex && plan.MandatoryFragments.Count == 0 && plan.NeedsUnicodeDecode &&
                    stats.RegexInvocations == 0, "Unsafe literal supplied byte constraints or changed verifier kind.");
                foreach (var hit in hits.Values)
                    Require(!snapshot.Materialize(hit, 0, 0).MatchText.Contains('�'), "Literal hit split a UTF-8 scalar.");
            }
        }
    }

    private static async Task VerifyContextAndDecodeDiagnostics(ISiftRegexEngine? engine)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            RegexEngine = engine, MaxMaterializedContextBytes = 7, TargetSegmentDocumentCount = 1
        });
        await index.Build(new Documents([("unicode.txt", "before\rXYZ\nneedle\ncafé alpha\n"),
            ("ascii.txt", "alpha\n")]));
        using var snapshot = index.OpenSnapshot();
        var hits = new Hits();
        snapshot.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new(), hits);
        var hit = hits.Values.Single();
        Require(snapshot.Materialize(hit, 1, 0).BeforeContext.SequenceEqual(["before\r"]) &&
            snapshot.MaterializeWithMatchingLine(hit, 1, 0).Hit.BeforeContext.SequenceEqual(["before\r"]),
            "Context clipping discarded an embedded carriage return.");
        foreach (var query in new[] { new SearchQuery("café", PatternMode.Literal, CaseMode.Sensitive),
            new SearchQuery(@"caf\u00E9", PatternMode.Regex, CaseMode.Sensitive),
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive) { WholeWord = true } })
        {
            var stats = snapshot.Search(query, new(), new Hits());
            Require(stats.HitsReturned > 0 && stats.DecodedChars > 0 && snapshot.Explain(query, new()).NeedsUnicodeDecode,
                "Decode diagnostic disagrees with Unicode literal verification.");
        }
        var ascii = new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive)
        {
            WholeWord = true, Multiline = true, DotAll = true
        };
        var asciiFilter = new PathFilter { PathPrefix = "ascii.txt" };
        Require(!snapshot.Explain(ascii, asciiFilter).NeedsUnicodeDecode &&
            snapshot.Search(ascii, asciiFilter, new Hits()).DecodedChars == 0,
            "ASCII literal flags incorrectly require decoding.");
        var missing = new PathFilter { PathPrefix = "missing/" };
        Require(!snapshot.Explain(new SearchQuery("alp.a", PatternMode.Regex, CaseMode.Sensitive), missing).NeedsUnicodeDecode,
            "Empty path scope requires decoding.");
    }

    private static void VerifyRegexValidation(IIndexSnapshot snapshot)
    {
        foreach (var pattern in new[] { "(?<1a>foo)", "(?<0>foo)", "(?<x!>foo)", "(?'x!'foo)", "*", "{1}" })
        foreach (var caseMode in new[] { CaseMode.Sensitive, CaseMode.Insensitive, CaseMode.Smart })
        {
            var query = new SearchQuery(pattern, PatternMode.Regex, caseMode);
            var filter = new PathFilter { PathPrefix = "missing/" };
            RequireInvalidRegex(() => snapshot.Search(query, filter, new Hits()));
            RequireInvalidRegex(() => snapshot.Explain(query, filter));
        }
        var valid = new SearchQuery("(?<word>alpha)", PatternMode.Regex, CaseMode.Sensitive);
        var hits = new Hits();
        snapshot.Search(valid, new(), hits);
        var plan = snapshot.Explain(valid, new());
        Require(hits.Values.Count == 1 && snapshot.Materialize(hits.Values[0], 0, 0).MatchText == "alpha",
            "Valid capture syntax lost matching semantics.");
        Require(!plan.CanUseLiteralVerifier && plan.UsesIndex && plan.MandatoryFragments.Contains("alpha"),
            "Valid captures lost managed validation or sound indexing.");
    }

    private static void RequireInvalidRegex(Action action)
    {
        try { action(); }
        catch (RegexParseException) { return; }
        throw new InvalidOperationException("Invalid regex syntax bypassed .NET validation.");
    }

    private static async Task VerifyGlobFilters(ISiftRegexEngine? engine)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new Documents([("src/file1.txt\n", "needle"), ("src/a\nb/file1.txt", "needle"),
            ("src/file1.txt", "needle"), ("src/file\n.txt", "needle")]));
        using var snapshot = index.OpenSnapshot();
        foreach (var pattern in new[] { "", "needle" })
        foreach (var (filter, expected) in new[]
        {
            (new PathFilter { IncludeGlobs = ["src/file?.txt"] },
                new[] { "src/file\n.txt", "src/file1.txt" }),
            (new PathFilter { IncludeGlobs = ["src/**/file?.txt"] },
                new[] { "src/a\nb/file1.txt" }),
            (new PathFilter { ExcludeGlobs = ["src/file?.txt"] },
                new[] { "src/a\nb/file1.txt", "src/file1.txt\n" })
        })
        {
            var hits = new Hits();
            snapshot.Search(new SearchQuery(pattern, PatternMode.Literal, CaseMode.Sensitive), filter, hits);
            Require(hits.Values.Select(hit => snapshot.Materialize(hit, 0, 0).Path).Order(StringComparer.Ordinal)
                .SequenceEqual(expected), "Glob filters changed whole-path or newline semantics.");
        }
    }

    private static async Task VerifyUnicodeAndEmptyQueries(ISiftRegexEngine? engine)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            RegexEngine = engine,
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(new Documents(new[] { ("hello😀.txt", "😀😁"), ("hello😁.txt", "😀") }
            .Concat(Enumerable.Range(0, 64).Select(i => ($"noise/{i:D2}.txt", ""))).ToArray()));
        using var snapshot = index.OpenSnapshot();
        var hits = new Hits();
        snapshot.Search(new SearchQuery("😀|😁", PatternMode.Regex, CaseMode.Sensitive), new(), hits);
        Require(hits.Values.Select(hit => (hit.StartByte, hit.EndByte))
            .SequenceEqual([(0, 4), (4, 8), (0, 4)]), "Unicode alternatives lost matches to byte pruning.");
        hits.Values.Clear();
        snapshot.Search(new SearchQuery("\uD83D", PatternMode.Regex, CaseMode.Sensitive), new(), hits);
        Require(hits.Values.Select(hit => (hit.StartByte, hit.EndByte))
            .SequenceEqual([(0, 0), (4, 4), (0, 0)]), "Surrogate regex returned invalid UTF-8 byte ranges.");
        hits.Values.Clear();
        snapshot.Search(new SearchQuery("", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathRegex = "hello😀|hello😁" }, hits);
        Require(hits.Values.Count == 2, "Unicode path regex lost matches to byte pruning.");

        var cacheHits = SiftProcessMetrics.RegexCacheHits;
        var cacheMisses = SiftProcessMetrics.RegexCacheMisses;
        var empty = new SearchQuery("", PatternMode.Regex, CaseMode.Sensitive)
        {
            WholeWord = true, WholeLine = true, Multiline = true, DotAll = true
        };
        var filter = new PathFilter { PathPrefix = "hello" };
        var stats = snapshot.Search(empty, filter, new Hits());
        var plan = snapshot.Explain(empty, filter);
        Require(stats.HitsReturned == 2 && stats.BytesScanned == 0 && stats.DecodedChars == 0 &&
            stats.RegexInvocations == 0 && stats.Utf8RegexInvocations == 0, "Empty query no longer lists paths directly.");
        Require(!plan.NeedsUnicodeDecode && !plan.MayRequireWholeDocumentVerification &&
            plan.RegexRouteReason == SiftRegexRouteReason.Literal, "Empty-query diagnostics require unused verification.");
        Require(cacheHits == SiftProcessMetrics.RegexCacheHits && cacheMisses == SiftProcessMetrics.RegexCacheMisses,
            "Empty query accessed the managed regex compilation cache.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Documents((string Path, string Text)[] items, Exception? failure = null, Action? beforeRead = null) : IDocumentSource
    {
        private int _position;
        public bool TryReadNext(out SubmittedDocument document)
        {
            if (_position == 0) beforeRead?.Invoke();
            document = default;
            if (_position == items.Length)
            {
                if (failure is not null) throw failure;
                return false;
            }
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

    private sealed class ThrowingCollector(Exception failure) : IHitCollector
    {
        public int Calls { get; private set; }
        public bool OnHit(in RawHit hit) { Calls++; throw failure; }
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
