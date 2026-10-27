using System.Text;
using System.Text.RegularExpressions;

namespace Lokad.Sift.Tests;

public sealed class CandidateVisitTests
{
    [Theory]
    [InlineData("snapshot")]
    [InlineData("overlay")]
    [InlineData("existing")]
    public async Task CandidatesPreserveExactFieldMatchesAndTraversalOrder(string route)
    {
        var documents = Enumerable.Range(0, 300).ToDictionary(n => $"src/noise-{n:D3}.txt", _ => "header\nordinary periodic aaaaaa");
        documents.Add("src/a.txt", "header\namber");
        documents.Add("src/b.txt", "amber header\nxamber\ncobalt\n");
        documents.Add("src/漢😀.txt", "é😀\nCAFÉ ſ ı İ K word amber 😀");
        documents.Add("src/empty.txt", "header\n");
        documents.Add("src/drop.txt", "header\namber");
        documents.Add("docs/a.txt", "header\ncobalt");
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            TargetSegmentDocumentCount = 50,
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0
        });
        await index.Build(new InMemoryDocumentSource(documents.Select(x => (x.Key, x.Value)).ToArray()));
        using var owner = route == "overlay" ? (IIndexSnapshot)index.OpenOverlaySnapshot() :
            index.OpenSnapshot();
        IIndexSnapshot snapshot = route == "existing" ? new ExistingSnapshot(owner) : owner;
        foreach (var (pattern, mode, casing) in new[]
        {
            ("amber|cobalt", PatternMode.Regex, CaseMode.Sensitive),
            ("AMBER|cobalt", PatternMode.Regex, CaseMode.Insensitive),
            (@"\Aamber", PatternMode.Regex, CaseMode.Sensitive),
            (@"amber\z", PatternMode.Regex, CaseMode.Sensitive),
            (@"(?<=x)amber", PatternMode.Regex, CaseMode.Sensitive),
            (@"\bword\b", PatternMode.Regex, CaseMode.Insensitive),
            ("I|S|K", PatternMode.Regex, CaseMode.Insensitive),
            ("(?:)", PatternMode.Regex, CaseMode.Sensitive),
            ("", PatternMode.Regex, CaseMode.Sensitive),
            ("missing-synthetic-marker", PatternMode.Regex, CaseMode.Sensitive),
            ("a", PatternMode.Literal, CaseMode.Sensitive),
            ("CAFÉ", PatternMode.Literal, CaseMode.Insensitive),
            ("\ud83d", PatternMode.Literal, CaseMode.Sensitive)
        })
        foreach (var exactPaths in new[] { false, true })
        {
            var filter = new PathFilter
            {
                PathPrefix = "src/", ExcludeGlobs = ["src/drop*"], PathRegex = @"\.txt$",
                IncludeGlobs = exactPaths ? ["src/a.txt", "src/漢😀.txt"] : []
            };
            var query = new SearchQuery(pattern, mode, casing) { Multiline = true, MaxHits = 1 };
            var regex = mode == PatternMode.Regex ? new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline |
                (casing == CaseMode.Insensitive ? RegexOptions.IgnoreCase : RegexOptions.None)) : null;
            bool Matches(string text)
            {
                var newline = text.IndexOf('\n');
                var field = newline < 0 ? "" : text[(newline + 1)..];
                return regex?.IsMatch(field) ?? field.Contains(pattern,
                    casing == CaseMode.Insensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            bool InScope(string path) => path.StartsWith("src/", StringComparison.Ordinal) && path != "src/drop.txt" &&
                (!exactPaths || path is "src/a.txt" or "src/漢😀.txt");
            var expected = documents.Where(x => InScope(x.Key) && Matches(x.Value)).Select(x => x.Key).Order().ToArray();
            var values = new List<CandidateDocument>();
            var stats = snapshot.VisitCandidates(query, filter, new Collector(document => { values.Add(document); return true; }));
            Assert.True(stats.Completed);
            Assert.Equal(values.Count, stats.VisitedDocuments);
            Assert.Equal(values.Sum(x => (long)x.Content.Length), stats.ContentBytes);
            Assert.Equal(values.Count, values.Select(x => x.Key).Distinct().Count());
            Assert.All(values, x => Assert.Equal(documents[x.Key.Path], Encoding.UTF8.GetString(x.Content.Span)));
            Assert.Equal(expected, values.Where(x => Matches(Encoding.UTF8.GetString(x.Content.Span))).Select(x => x.Key.Path).Order());
            var listing = new HitBuffer();
            snapshot.Search(new SearchQuery("") { MaxHits = int.MaxValue, MaxHitsPerDocument = 1 }, filter, listing);
            var candidatePaths = values.Select(x => x.Key.Path).ToHashSet();
            Assert.Equal(listing.Hits.Select(x => snapshot.GetDocumentKey(x).Path).Where(candidatePaths.Contains), values.Select(x => x.Key.Path));
        }
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("overlay")]
    [InlineData("existing")]
    public async Task LimitsCancellationAndCallbackFailuresRemainExplicit(string route)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { TargetSegmentDocumentCount = 1 });
        await index.Build(new InMemoryDocumentSource([("a", "alpha"), ("b", "alpha"), ("c", "alpha")]));
        using var owner = route == "overlay" ? (IIndexSnapshot)index.OpenOverlaySnapshot() :
            index.OpenSnapshot();
        IIndexSnapshot snapshot = route == "existing" ? new ExistingSnapshot(owner) : owner;
        var query = new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive) { MaxHits = 1, MaxMatchedDocuments = 1, MaxBytesScanned = 1 };
        var all = snapshot.VisitCandidates(query, new(), new Collector(_ => true));
        Assert.Equal(3, all.VisitedDocuments); // Search hit limits are deliberately independent.
        Assert.True(all.Completed);
        var limited = snapshot.VisitCandidates(query, new(), new Collector(_ => true), new CandidateVisitOptions { MaxDocuments = 1 });
        Assert.Equal((1, CandidateVisitStopReason.MaxDocuments), (limited.VisitedDocuments, limited.StopReason));
        Assert.False(limited.Completed);
        var bytes = snapshot.VisitCandidates(query, new(), new Collector(_ => true), new CandidateVisitOptions { MaxContentBytes = 5 });
        Assert.Equal((1, 5L, CandidateVisitStopReason.MaxContentBytes), (bytes.VisitedDocuments, bytes.ContentBytes, bytes.StopReason));
        var stopped = snapshot.VisitCandidates(query, new(), new Collector(_ => false));
        Assert.Equal((1, CandidateVisitStopReason.CollectorStopped), (stopped.VisitedDocuments, stopped.StopReason));
        using var cancellation = new CancellationTokenSource();
        var cancelled = new Collector(_ => { cancellation.Cancel(); return false; });
        var failure = Assert.ThrowsAny<OperationCanceledException>(() => snapshot.VisitCandidates(query, new(), cancelled, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        foreach (var error in new Exception[] { new IOException("Callback I/O"), new InvalidDataException("Callback data"), new RegexMatchTimeoutException() })
        {
            Assert.Same(error, Record.Exception(() => snapshot.VisitCandidates(query, new(), new Collector(_ => throw error))));
            Assert.False(index.RequiresRebuild);
        }
        var nested = false;
        Task<Exception?>? overlayFailure = null;
        snapshot.VisitCandidates(query, new(), new Collector(_ =>
        {
            if (!nested) { nested = true; Assert.True(snapshot.VisitCandidates(query, new(), new Collector(_ => true)).Completed); }
            Assert.Throws<InvalidOperationException>(owner.Dispose);
            if (owner is IOverlaySnapshot overlay)
                overlayFailure = Record.ExceptionAsync(() => overlay.Update(new InMemoryDocumentSource([]), [], CancellationToken.None));
            return false;
        }));
        Assert.True(nested);
        if (overlayFailure is not null) Assert.IsType<InvalidOperationException>(await overlayFailure);
    }

    [Fact]
    public async Task DeadlineAndPreparationFailuresDoNotBecomeSuccessfulVisits()
    {
        using var engine = new RegexContractTestEngine { BeforeMatch = () => throw new Exception("Candidate visits must not match content.") };
        using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = engine });
        await index.Build(new InMemoryDocumentSource([("a", "alpha"), ("b", "alpha")]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("alp.a", PatternMode.Regex, CaseMode.Sensitive);
        Assert.True(snapshot.VisitCandidates(query, new(), new Collector(_ => true)).Completed);
        var deadline = snapshot.VisitCandidates(query, new(), new Collector(_ => { Thread.Sleep(100); return true; }),
            new CandidateVisitOptions { MaxElapsedMilliseconds = 50 });
        Assert.Equal(CandidateVisitStopReason.MaxElapsedTime, deadline.StopReason);
        Assert.Equal(1, deadline.VisitedDocuments);
        Assert.ThrowsAny<ArgumentException>(() => snapshot.VisitCandidates(new SearchQuery("["), new(), new Collector(_ => true)));
        foreach (var options in new[] { new CandidateVisitOptions { MaxDocuments = 0 }, new CandidateVisitOptions { MaxContentBytes = 0 }, new CandidateVisitOptions { MaxElapsedMilliseconds = 0 } })
            Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.VisitCandidates(query, new(), new Collector(_ => true), options));
        engine.TimeoutCompile = true;
        Assert.Throws<RegexMatchTimeoutException>(() => snapshot.VisitCandidates(new SearchQuery("bet.a"), new(), new Collector(_ => true)));
        Assert.False(index.RequiresRebuild);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedContentAndVisibilitySurviveUpdatesAndCompaction(bool disk)
    {
        using var fixture = new CorpusFixture();
        var options = new SiftOptions { TargetSegmentDocumentCount = 1 };
        using var index = disk ? new SiftIndex(fixture.CorpusPath, options) : SiftIndex.CreateInMemory(options);
        await index.Build(new InMemoryDocumentSource([("a", "header\namber"), ("b", "header\ncobalt")]));
        using var retained = index.OpenSnapshot();
        using var overlay = index.OpenOverlaySnapshot();
        CandidateDocument borrowed = default;
        retained.VisitCandidates(new SearchQuery(""), new(), new Collector(x => { borrowed = x; return false; }));
        await index.Update(new InMemoryDocumentSource([("a", "new\nempty"), ("c", "new\namber")]), [new("b")]);
        await overlay.Update(new InMemoryDocumentSource([("a", "overlay\ncobalt"), ("d", "overlay\namber")]), [new("b")]);
        CandidateDocument overlayBytes = default;
        overlay.VisitCandidates(new SearchQuery(""), new(), new Collector(x => { if (x.Key.Path == "a") overlayBytes = x; return true; }));
        await overlay.Update(new InMemoryDocumentSource([("a", "changed\nnew")]), []);
        await index.Compact(new CompactOptions { MinimumSegmentCountForSmallMerge = 1 });
        Assert.Equal("header\namber", Encoding.UTF8.GetString(borrowed.Content.Span));
        Assert.Equal("overlay\ncobalt", Encoding.UTF8.GetString(overlayBytes.Content.Span));
        using var current = index.OpenSnapshot();
        foreach (var (view, paths) in new[] { (retained, new[] { "a", "b" }), (current, new[] { "a", "c" }), ((IIndexSnapshot)overlay, new[] { "a", "d" }) })
        {
            var actual = new List<string>();
            Assert.True(view.VisitCandidates(new SearchQuery(""), new(), new Collector(x => { actual.Add(x.Key.Path); return true; })).Completed);
            Assert.Equal(paths, actual.Order());
        }
        retained.Dispose();
        Assert.Equal("a", borrowed.Key.Path);
        Assert.Throws<ObjectDisposedException>(() => retained.VisitCandidates(new SearchQuery(""), new(), new Collector(_ => true)));
    }

    [Fact]
    public async Task CorruptionStillMarksTheIndexForRebuild()
    {
        using var fixture = new CorpusFixture(("a", "alpha"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var path = Directory.EnumerateFiles(Path.Combine(fixture.CorpusPath, "segments"), "segment.pack", SearchOption.AllDirectories).Single();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) stream.WriteByte(0);
        using var snapshot = index.OpenSnapshot();
        Assert.Throws<InvalidDataException>(() => snapshot.VisitCandidates(new SearchQuery(""), new(), new Collector(_ => true)));
        Assert.True(index.RequiresRebuild);
    }

    private sealed class Collector(Func<CandidateDocument, bool> accept) : IDocumentCandidateCollector
    {
        public bool OnCandidate(in CandidateDocument document) => accept(document);
    }

    // Compiles against the pre-existing contract; the new default supplies a sound listing.
    private sealed class ExistingSnapshot(IIndexSnapshot inner) : IIndexSnapshot
    {
        public IndexIdentity Identity => inner.Identity;
        public ReadOnlyMemory<byte> CommitMetadata => inner.CommitMetadata;
        public DocumentVisitStats VisitDocuments(IDocumentKeyCollector collector, CancellationToken token) => inner.VisitDocuments(collector, token);
        public SearchPlan Explain(SearchQuery query, PathFilter filter) => inner.Explain(query, filter);
        public SearchStats Search(SearchQuery query, PathFilter filter, IHitCollector collector, CancellationToken token) => inner.Search(query, filter, collector, token);
        public MaterializedHit Materialize(in RawHit hit, int before, int after) => inner.Materialize(hit, before, after);
        public MaterializedHitWithLine MaterializeWithMatchingLine(in RawHit hit, int before, int after) => inner.MaterializeWithMatchingLine(hit, before, after);
        public bool TryGetDocumentBytes(DocumentKey key, out ReadOnlyMemory<byte> content) => inner.TryGetDocumentBytes(key, out content);
        public void Dispose() { }
    }
}
