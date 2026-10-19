using System.Text;
using System.Text.RegularExpressions;
using Lokad.Sift.Examples;
using Lokad.Sift.Search;
using ProducerRegex = Lokad.Utf8Regex.Utf8Regex;

namespace Lokad.Sift.Tests;

[Collection("ProcessMetrics")]
public sealed class RegexAdapterTests
{
    private static readonly SearchQuery NativeQuery = new("alpha[0-9]+", PatternMode.Regex, CaseMode.Sensitive);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompilationCannotDisposeItsActiveSnapshot(bool explain, bool overlay)
    {
        var engine = new RegexContractTestEngine();
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using IIndexSnapshot snapshot = overlay ? index.OpenOverlaySnapshot() : index.OpenSnapshot();
        engine.BeforeCompile = () => Assert.Throws<InvalidOperationException>(snapshot.Dispose);

        if (explain)
            Assert.Equal(SiftRegexRouteReason.Native, snapshot.Explain(NativeQuery, new()).RegexRouteReason);
        else
            Assert.Equal(1, snapshot.Search(NativeQuery, new(), new HitBuffer()).HitsReturned);

        Assert.Equal(1, engine.Compilations);
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("a.txt"), out _));
        Assert.Equal(1, snapshot.Search(NativeQuery, new(), new HitBuffer()).HitsReturned);
        Assert.False(index.RequiresRebuild);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotDisposalWaitsForHostCompilation(bool explain)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposalStarted = new ManualResetEventSlim();
        var engine = new RegexContractTestEngine
        {
            BeforeCompile = () =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = index.OpenSnapshot();
        var request = Task.Run(() => explain
            ? snapshot.Explain(NativeQuery, new()).RegexRouteReason == SiftRegexRouteReason.Native
            : snapshot.Search(NativeQuery, new(), new HitBuffer()).HitsReturned == 1);
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            disposal = Task.Run(() => { disposalStarted.Set(); snapshot.Dispose(); });
            Assert.True(disposalStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotSame(disposal, await Task.WhenAny(disposal, Task.Delay(100)));
        }
        finally
        {
            release.Set();
            await request.WaitAsync(TimeSpan.FromSeconds(5));
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(await request);
        Assert.Throws<ObjectDisposedException>(() => snapshot.Search(NativeQuery, new(), new HitBuffer()));
        Assert.False(engine.Disposed);
    }

    [Fact]
    public async Task LiteralAndExactLiteralRoutesDoNotCompileTheSuppliedEngine()
    {
        var engine = new RegexContractTestEngine { FailCompile = true };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = index.OpenSnapshot();
        foreach (var query in new[]
        {
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new SearchQuery("alpha", PatternMode.Regex, CaseMode.Sensitive)
        })
            Assert.Equal(1, snapshot.Search(query, new(), new HitBuffer()).HitsReturned);
        Assert.Equal(0, engine.Compilations);
    }

    [Fact]
    public async Task EngineReceivesEffectivePatternOptionsAndFiniteTimeout()
    {
        var engine = new RegexContractTestEngine { NoMatches = true };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha")]));
        using var snapshot = index.OpenSnapshot();
        snapshot.Search(new SearchQuery("alp.a", PatternMode.Regex, CaseMode.Smart)
            { WholeWord = true, WholeLine = true, DotAll = true }, new(), new HitBuffer());
        var request = Assert.Single(engine.Requests);
        Assert.Equal(@"^(?:\b(?:alp.a)\b)$", request.Pattern);
        Assert.Equal(RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline,
            request.Options);
        Assert.Equal(TimeSpan.FromSeconds(1), request.Timeout);
    }

    [Fact]
    public async Task DefaultAndDecliningEnginesUseManagedVerification()
    {
        foreach (var engine in new ISiftRegexEngine?[] { null, new RegexContractTestEngine { Decline = true }, new Utf8RegexAdapter() })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
            await index.Build(new InMemoryDocumentSource([("a.txt", "alpha123")]));
            using var snapshot = index.OpenSnapshot();
            var plan = snapshot.Explain(NativeQuery, new());
            var hits = new HitBuffer();
            var stats = snapshot.Search(NativeQuery, new(), hits);
            Assert.Equal(engine is null ? SiftRegexRouteReason.NoEngine : SiftRegexRouteReason.EngineDeclined, plan.RegexRouteReason);
            Assert.Equal(SearchVerifierKind.RegexFallback, plan.VerifierKind);
            Assert.True(plan.CanUseNonBacktracking);
            Assert.Single(hits.Hits);
            Assert.Equal(0, stats.Utf8RegexInvocations);
            Assert.Equal(1, stats.NonBacktrackingRegexInvocations);
        }
    }

    [Theory]
    [InlineData("alp.a", "alpha", false)]
    [InlineData("red.*blue|blue.*red", "red A blue B blue\n", true)]
    [InlineData("\\b(?:this|that)\\b", "this that other", false)]
    [InlineData("a+?", "aaaa", false)]
    [InlineData("^alpha.*$", "alpha x\nalpha y\n", false)]
    [InlineData("caf[ée]", "café cafe", false)]
    public void RealProducerRemainsADifferentialOracle(string pattern, string text, bool ignoreCase)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.Multiline |
            (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        var producer = new ProducerRegex(pattern, options, TimeSpan.FromSeconds(1));
        var actual = new List<(int, int)>();
        foreach (var match in producer.EnumerateMatches(Encoding.UTF8.GetBytes(text)))
        {
            Assert.True(match.TryGetByteRange(out var start, out var length));
            actual.Add((start, length));
        }
        var expected = new Regex(pattern, options, TimeSpan.FromSeconds(1)).Matches(text)
            .Select(match => (Encoding.UTF8.GetByteCount(text.AsSpan(0, match.Index)),
                Encoding.UTF8.GetByteCount(text.AsSpan(match.Index, match.Length)))).ToArray();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task HiddenBacktrackingReproductionUsesSafeFallback(bool unicode, bool missingTail, bool positive)
    {
        const string pattern = "RED.*blue|blue.*RED|RED.*green|green.*RED|zero.*RED|reset.*RED|old.*yellow|prior.*yellow";
        var repeated = string.Concat(Enumerable.Repeat("RED ", 65_536));
        var text = positive ? "red A blue B blue\n" : repeated +
            (missingTail ? "" : unicode ? "\nblue green yellow é\n" : "\nblue green yellow\n");
        var options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking;
        Assert.Equal(positive, new Regex(pattern, options, TimeSpan.FromSeconds(1)).IsMatch(text));
        foreach (var engine in new ISiftRegexEngine?[] { null, new Utf8RegexAdapter() })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
            await index.Build(new InMemoryDocumentSource([("docs/sample.txt", text)]));
            using var snapshot = index.OpenSnapshot();
            var query = new SearchQuery(pattern, PatternMode.Regex, CaseMode.Insensitive)
                { MaxHits = int.MaxValue, MaxHitsPerDocument = 1, RegexTimeoutPolicy = RegexTimeoutPolicy.Throw };
            var filter = new PathFilter { PathPrefix = "docs/" };
            var plan = snapshot.Explain(query, filter);
            var hits = new HitBuffer();
            var stats = snapshot.Search(query, filter, hits);
            Assert.Equal(SearchStopReason.None, stats.StopReason);
            Assert.Equal(SearchVerifierKind.RegexFallback, plan.VerifierKind);
            Assert.Equal(0, stats.Utf8RegexInvocations);
            Assert.Equal(missingTail ? 0 : 1, stats.NonBacktrackingRegexInvocations);
            Assert.Equal(positive ? 1 : 0, hits.Hits.Count);
            if (positive) Assert.Equal("red A blue B blue", snapshot.Materialize(hits.Hits[0], 0, 0).MatchText);
        }
    }

    [Fact]
    public async Task NativeCompiledPatternSurvivesSnapshotTurnoverGenerationsAndIndexDisposal()
    {
        var engine = new RegexContractTestEngine();
        var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using (var snapshot = index.OpenSnapshot())
        {
            Assert.Equal(SiftRegexRouteReason.Native, snapshot.Explain(NativeQuery, new()).RegexRouteReason);
            var stats = snapshot.Search(NativeQuery, new(), new HitBuffer());
            Assert.Equal(1, stats.Utf8RegexInvocations);
            Assert.Equal(1, stats.Utf8RegexCacheHits);
            Assert.Equal(0, stats.Utf8RegexCacheMisses);
        }
        Assert.False(Assert.Single(engine.Patterns).Disposed);
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha2")]));
        using (var overlay = index.OpenOverlaySnapshot())
            Assert.Equal(1, overlay.Search(NativeQuery, new(), new HitBuffer()).HitsReturned);
        var surviving = index.OpenSnapshot();
        index.Dispose();
        Assert.Equal(1, surviving.Search(NativeQuery, new(), new HitBuffer()).HitsReturned);
        Assert.Equal(1, engine.Compilations);
        Assert.False(Assert.Single(engine.Patterns).Disposed);
        surviving.Dispose();
        Assert.True(Assert.Single(engine.Patterns).Disposed);
        Assert.False(engine.Disposed);
    }

    [Fact]
    public async Task DifferentBindingsNeverShareCompiledPatternsOrDeclines()
    {
        var native = new RegexContractTestEngine();
        var declining = new RegexContractTestEngine { Decline = true };
        foreach (var engine in new ISiftRegexEngine?[] { native, declining, null, native })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
            await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
            using var snapshot = index.OpenSnapshot();
            var hits = new HitBuffer();
            var stats = snapshot.Search(NativeQuery, new(), hits);
            Assert.Single(hits.Hits);
            Assert.Equal(ReferenceEquals(engine, native) ? 1 : 0, stats.Utf8RegexInvocations);
        }
        Assert.Equal(2, native.Compilations);
        Assert.Equal(1, declining.Compilations);
        Assert.All(native.Patterns, pattern => Assert.True(pattern.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeInputDomainFallbackHasTruthfulExplanationAndCounters(bool includeAscii)
    {
        var documents = new List<(string, string)> { ("unicode.txt", "alphaé") };
        if (includeAscii) documents.Add(("ascii.txt", "alpha1"));
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = new RegexContractTestEngine() });
        await index.Build(new InMemoryDocumentSource(documents));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("alpha[0-9]*", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new());
        var stats = snapshot.Search(query, new(), new HitBuffer());
        Assert.True(plan.NeedsUnicodeDecode);
        Assert.Equal(includeAscii ? SiftRegexRouteReason.MixedInputDomains : SiftRegexRouteReason.InputOutsideDomain, plan.RegexRouteReason);
        Assert.Equal(includeAscii ? SearchVerifierKind.Utf8RegexWithUnicodeFallback : SearchVerifierKind.RegexFallback, plan.VerifierKind);
        Assert.Equal(includeAscii ? 1 : 0, stats.Utf8RegexInvocations);
        Assert.Equal(1, stats.NonBacktrackingRegexInvocations);
        Assert.Equal(includeAscii ? 2 : 1, stats.HitsReturned);
        using var overlay = index.OpenOverlaySnapshot();
        Assert.Equal(plan.RegexRouteReason, overlay.Explain(query, new()).RegexRouteReason);
    }

    [Fact]
    public async Task EvictionDefersDisposalUntilActiveSearchReturns()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = new RegexContractTestEngine
        {
            BeforeMatch = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
        };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = index.OpenSnapshot();
        var searching = Task.Run(() => snapshot.Search(NativeQuery, new(), new HitBuffer()));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var active = Assert.Single(engine.Patterns);
        try
        {
            for (var i = 0; i < SearchQueryPreparer.CacheCapacity + 1; i++)
                snapshot.Explain(new SearchQuery($"alpha[0-9]*{i}", PatternMode.Regex, CaseMode.Sensitive), new());
            Assert.False(active.Disposed);
            Assert.Contains(engine.Patterns, pattern => pattern.Disposed);
        }
        finally { release.Set(); }
        Assert.Equal(1, (await searching).HitsReturned);
        Assert.True(active.Disposed);
    }

    [Fact]
    public async Task BoundCacheAccountsForEffectiveWrappedPatternCharacters()
    {
        var engine = new RegexContractTestEngine();
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = index.OpenSnapshot();
        var prefix = new string('a', 1800);
        for (var i = 0; i < 160; i++)
            snapshot.Explain(new SearchQuery($"{prefix}[0-9]*{i}", PatternMode.Regex, CaseMode.Sensitive)
                { WholeWord = true, WholeLine = true }, new());
        var retained = engine.Patterns.Where(pattern => !pattern.Disposed).ToArray();
        Assert.NotEmpty(retained);
        Assert.True(retained.Length < engine.Compilations);
        Assert.True(retained.Length <= SearchQueryPreparer.CacheCapacity);
        var actualCharacters = retained.Sum(pattern => pattern.PatternText.Length);
        Assert.True(actualCharacters <= SearchQueryPreparer.CacheCharacterCapacity);
        Assert.Equal(actualCharacters, SiftProcessMetrics.Utf8RegexCachePatternCharacters);
    }

    [Fact]
    public async Task InvalidRangesAndUnexpectedFailuresDoNotRequestFallback()
    {
        foreach (var engine in new[] { new RegexContractTestEngine { InvalidRange = true }, new RegexContractTestEngine { FailCompile = true } })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
            await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
            using var snapshot = index.OpenSnapshot();
            var hits = new HitBuffer();
            if (engine.InvalidRange)
                Assert.Throws<InvalidOperationException>(() => snapshot.Search(NativeQuery, new(), hits));
            else
                Assert.Throws<IOException>(() => snapshot.Search(NativeQuery, new(), hits));
            Assert.Empty(hits.Hits);
            Assert.Equal(1, engine.Compilations);
        }
    }

    [Fact]
    public async Task InvalidCompilationResultsFailAndReleaseOwnedHandles()
    {
        foreach (var engine in new[]
        {
            new RegexContractTestEngine { ReturnNullOnSuccess = true },
            new RegexContractTestEngine { ReturnHandleOnDecline = true },
            new RegexContractTestEngine { InvalidDomain = true }
        })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
            await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
            using var snapshot = index.OpenSnapshot();
            var hits = new HitBuffer();
            Assert.Throws<InvalidOperationException>(() => snapshot.Search(NativeQuery, new(), hits));
            Assert.Empty(hits.Hits);
            Assert.All(engine.Patterns, pattern => Assert.True(pattern.Disposed));
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ProviderFailuresDoNotMarkTheIndexCorrupt(bool duringCompilation, bool overlay)
    {
        var failure = new InvalidDataException("Provider failure, unrelated to index storage.");
        var engine = new RegexContractTestEngine();
        if (duringCompilation) engine.BeforeCompile = () => throw failure;
        else engine.BeforeMatch = () => throw failure;
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = overlay ? index.OpenOverlaySnapshot() : index.OpenSnapshot();
        if (duringCompilation)
            Assert.Same(failure, Assert.Throws<InvalidDataException>(() => snapshot.Explain(NativeQuery, new())));
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() => snapshot.Search(NativeQuery, new(), new HitBuffer())));
        Assert.False(index.RequiresRebuild);
        Assert.Equal(1, snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive), new(), new HitBuffer()).HitsReturned);
    }

    [Fact]
    public async Task CancellationAfterCompileReleasesTheReturnedHandle()
    {
        using var cancellation = new CancellationTokenSource();
        var engine = new RegexContractTestEngine { AfterCompile = cancellation.Cancel };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = index.OpenSnapshot();
        var error = Assert.ThrowsAny<OperationCanceledException>(() =>
            snapshot.Search(NativeQuery, new(), new HitBuffer(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(Assert.Single(engine.Patterns).Disposed);
    }

    [Fact]
    public async Task PartialNativeTimeoutKeepsPublishedHitsAndNeverRestartsFallback()
    {
        var engine = new RegexContractTestEngine { TimeoutAfterMatch = true };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = index.OpenSnapshot();
        var hits = new HitBuffer();
        var stats = snapshot.Search(NativeQuery, new(), hits);
        Assert.Single(hits.Hits);
        Assert.Equal(1, stats.HitsReturned);
        Assert.Equal(SearchStopReason.RegexTimeout, stats.StopReason);
        Assert.Equal(1, stats.Utf8RegexMatchesEnumerated);
        Assert.Equal(1, stats.Utf8RegexMatchedDocuments);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Theory]
    [InlineData(RegexTimeoutPolicy.StopSearch)]
    [InlineData(RegexTimeoutPolicy.Throw)]
    public async Task MatchingTimeoutPreservesQueryPolicy(RegexTimeoutPolicy policy)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
            { RegexEngine = new RegexContractTestEngine { TimeoutMatch = true } });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = index.OpenSnapshot();
        var query = NativeQuery with { RegexTimeoutPolicy = policy };
        if (policy == RegexTimeoutPolicy.Throw)
            Assert.Throws<RegexMatchTimeoutException>(() => snapshot.Search(query, new(), new HitBuffer()));
        else
            Assert.Equal(SearchStopReason.RegexTimeout, snapshot.Search(query, new(), new HitBuffer()).StopReason);
    }

    [Theory]
    [InlineData(RegexTimeoutPolicy.StopSearch, false)]
    [InlineData(RegexTimeoutPolicy.Throw, false)]
    [InlineData(RegexTimeoutPolicy.StopSearch, true)]
    [InlineData(RegexTimeoutPolicy.Throw, true)]
    public async Task CompilationTimeoutPreservesQueryPolicy(RegexTimeoutPolicy policy, bool overlay)
    {
        var engine = new RegexContractTestEngine { TimeoutCompile = true };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = overlay ? index.OpenOverlaySnapshot() : index.OpenSnapshot();
        var query = NativeQuery with { RegexTimeoutPolicy = policy };
        if (policy == RegexTimeoutPolicy.Throw)
            Assert.Throws<RegexMatchTimeoutException>(() => snapshot.Search(query, new(), new HitBuffer()));
        else
            Assert.Equal(SearchStopReason.RegexTimeout, snapshot.Search(query, new(), new HitBuffer()).StopReason);
        Assert.Equal(1, engine.Compilations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderCancellationPreservesCallerToken(bool duringCompilation)
    {
        using var cancellation = new CancellationTokenSource();
        var engine = new RegexContractTestEngine();
        if (duringCompilation) engine.BeforeCompile = cancellation.Cancel;
        else engine.BeforeMatch = cancellation.Cancel;
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = index.OpenSnapshot();
        var exception = Assert.ThrowsAny<OperationCanceledException>(() =>
            snapshot.Search(NativeQuery, new(), new HitBuffer(), cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task ProviderCheckpointIncludesPreparationDeadline()
    {
        var engine = new RegexContractTestEngine { BeforeCompile = () => Thread.Sleep(150) };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = index.OpenSnapshot();
        var stats = snapshot.Search(NativeQuery with { MaxElapsedMilliseconds = 30 }, new(), new HitBuffer());
        Assert.Equal(SearchStopReason.MaxElapsedTime, stats.StopReason);
        Assert.Equal(0, stats.HitsReturned);
        Assert.Empty(engine.Patterns);
    }

    [Fact]
    public async Task CancelledCompileDoesNotCancelHealthyCoalescedCaller()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var compileCalls = 0;
        var engine = new RegexContractTestEngine
        {
            BeforeCompile = () =>
            {
                if (Interlocked.Increment(ref compileCalls) != 1) return;
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = Assert.IsType<SearchSnapshot>(index.OpenSnapshot());
        var cancelled = Task.Run(() => snapshot.Search(NativeQuery, new(), new HitBuffer(), cancellation.Token));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var healthy = Task.Run(() => snapshot.Search(NativeQuery, new(), new HitBuffer()));
        try
        {
            Assert.True(SpinWait.SpinUntil(() => snapshot.RegexContext.ActiveQueryUsers == 2, TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
        }
        finally { release.Set(); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, (await healthy).HitsReturned);
        Assert.Equal(2, engine.Compilations);
    }

    [Fact]
    public async Task WaitingCallerHonorsItsOwnDeadlineWithoutEvictingAHealthyCompile()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = new RegexContractTestEngine
        {
            BeforeCompile = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
        };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = index.OpenSnapshot();
        var compiling = Task.Run(() => snapshot.Search(NativeQuery, new(), new HitBuffer()));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var waiting = Task.Run(() => snapshot.Search(NativeQuery with { MaxElapsedMilliseconds = 30 }, new(), new HitBuffer()));
        try
        {
            var stopped = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(SearchStopReason.MaxElapsedTime, stopped.StopReason);
            Assert.False(compiling.IsCompleted);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(compiling, waiting);
        }
        Assert.Equal(1, (await compiling).HitsReturned);
        Assert.Equal(1, snapshot.Search(NativeQuery, new(), new HitBuffer()).HitsReturned);
        Assert.Equal(1, engine.Compilations);
    }

    [Fact]
    public async Task RecursiveCompilationFailsWithoutWaitingOnItsOwnFactory()
    {
        var engine = new RegexContractTestEngine();
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha1")]));
        using var snapshot = index.OpenSnapshot();
        engine.BeforeCompile = () => snapshot.Search(NativeQuery with { MaxElapsedMilliseconds = 100 }, new(), new HitBuffer());
        Assert.Throws<InvalidOperationException>(() => snapshot.Search(NativeQuery, new(), new HitBuffer()));
    }

    [Fact]
    public async Task NativeNoMatchAndMisbehavingEarlyStopRemainObservable()
    {
        var engine = new RegexContractTestEngine { NoMatches = true };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "alpha")]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("alpha[0-9]+", PatternMode.Regex, CaseMode.Sensitive);
        Assert.Equal(0, snapshot.Search(query, new(), new HitBuffer()).HitsReturned);
        engine.NoMatches = false;
        engine.Ranges = [(0, 0), (1, 0)];
        engine.ContinueAfterStop = true;
        Assert.Throws<InvalidOperationException>(() => snapshot.Search(
            new SearchQuery("a*?", PatternMode.Regex, CaseMode.Sensitive) { MaxHits = 1 }, new(), new HitBuffer()));
    }

    [Fact]
    public async Task NativeEnumerationPreservesEmptyMatchesAndEarlyStop()
    {
        var engine = new RegexContractTestEngine { Ranges = [(0, 0), (1, 0), (2, 0)] };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "aa")]));
        using var snapshot = index.OpenSnapshot();
        var hits = new HitBuffer();
        var query = new SearchQuery("a*?", PatternMode.Regex, CaseMode.Sensitive) { MaxHits = 2 };
        var stats = snapshot.Search(query, new(), hits);
        Assert.Equal(2, hits.Hits.Count);
        Assert.Equal(SearchStopReason.MaxHits, stats.StopReason);
        Assert.Equal(2, stats.Utf8RegexMatchesEnumerated);
    }

    [Fact]
    public async Task NativeEnumerationHonorsDocumentLimitsAndCollectorStop()
    {
        var engine = new RegexContractTestEngine { Ranges = [(0, 1), (1, 1)] };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a.txt", "ab"), ("b.txt", "ab")]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("[ab]", PatternMode.Regex, CaseMode.Sensitive) { MaxHitsPerDocument = 1 };
        var stats = snapshot.Search(query, new(), new HitBuffer());
        Assert.Equal(2, stats.HitsReturned);
        Assert.Equal(2, stats.Utf8RegexInvocations);
        var limited = snapshot.Search(query with { MaxMatchedDocuments = 1 }, new(), new HitBuffer());
        Assert.Equal(1, limited.HitsReturned);
        Assert.Equal(SearchStopReason.MaxMatchedDocuments, limited.StopReason);
        var stopped = snapshot.Search(query, new(), new CallbackHitCollector(static () => { }));
        Assert.Equal(1, stopped.HitsReturned);
        Assert.Equal(SearchStopReason.CollectorStopped, stopped.StopReason);
    }
}
