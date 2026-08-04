using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Lokad.Sift;
using Lokad.Sift.Search;

namespace Lokad.Sift.Tests;

public sealed class SearchIntegrationTests
{
    [Fact]
    public async Task SearchRejectsInvalidModesAndExcessiveParallelism()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { MaxSegmentParallelism = 4 });
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            index.OpenSnapshot(new SearchOptions(segmentParallelism: 5)));
        using var snapshot = index.OpenSnapshot();

        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.Explain(
            new SearchQuery("alpha", (PatternMode)int.MaxValue, CaseMode.Sensitive),
            new PathFilter()));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.Explain(
            new SearchQuery("alpha", PatternMode.Literal, (CaseMode)int.MaxValue),
            new PathFilter()));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.Explain(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive)
            {
                TimeoutPolicy = (TimeoutPolicy)int.MaxValue
            },
            new PathFilter()));
    }

    [Fact]
    public async Task SearchRejectsOversizedQueryAndPathPatterns()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            MaxPatternCharacters = 8,
            MaxPathFilterPatterns = 2
        });
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        using var snapshot = index.OpenSnapshot();

        Assert.Throws<ArgumentException>(() => snapshot.Explain(
            new SearchQuery("123456789", PatternMode.Regex, CaseMode.Sensitive),
            new PathFilter()));
        Assert.Throws<ArgumentException>(() => snapshot.Explain(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathRegex = "123456789" }));
        Assert.Throws<ArgumentException>(() => snapshot.Explain(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter
            {
                IncludeGlobs = ["src/**", "test/**"],
                ExcludeGlobs = ["obj/**"]
            }));
    }

    [Fact]
    public async Task SearchRejectsDeeplyNestedRegexesBeforeCompilation()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            MaxPatternNesting = 8
        });
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        using var snapshot = index.OpenSnapshot();
        var nested = string.Concat(Enumerable.Repeat("(", 9)) + "alpha" + string.Concat(Enumerable.Repeat(")", 9));

        Assert.Throws<ArgumentException>(() => snapshot.Explain(
            new SearchQuery(nested, PatternMode.Regex, CaseMode.Sensitive),
            new PathFilter()));
        Assert.Throws<ArgumentException>(() => snapshot.Explain(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathRegex = nested }));
    }

    [Fact]
    public async Task InvalidLargeQuantifierDoesNotEscapeAsArithmeticOverflow()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        using var snapshot = index.OpenSnapshot();

        var exception = Record.Exception(() => snapshot.Search(
            new SearchQuery(
                "a{999999999999999999999999999999}",
                PatternMode.Regex,
                CaseMode.Sensitive),
            new PathFilter(),
            new HitBuffer()));

        Assert.IsAssignableFrom<ArgumentException>(exception);
        Assert.IsNotType<OverflowException>(exception);
    }

    [Fact]
    public async Task SearchRejectsOversizedAggregatePathFilter()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            MaxPatternCharacters = 8,
            MaxPathFilterCharacters = 10
        });
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha")]));
        using var snapshot = index.OpenSnapshot();

        Assert.Throws<ArgumentException>(() => snapshot.Explain(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { IncludeGlobs = ["src/**", "doc/**"] }));
    }

    [Fact]
    public async Task LiteralSearchMatchesBruteForceBaseline()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "Alpha beta\nbeta alpha\n"),
            ("src/b.txt", "gamma Beta delta\n"),
            ("notes/c.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(new SearchQuery("beta", PatternMode.Literal, CaseMode.Smart), new PathFilter(), collector);

        var expected = fixture.Documents
            .SelectMany(static doc => TestHelpers.FindAll(doc.Path, doc.Content, "beta", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static hit => hit.Path, StringComparer.Ordinal)
            .ThenBy(static hit => hit.Line)
            .ThenBy(static hit => hit.Column)
            .ToArray();

        var actual = collector.Hits
            .Select(hit => snapshot.Materialize(hit, 0, 0))
            .Select(static hit => (hit.Path, hit.StartLine, hit.StartColumn, hit.MatchText))
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task PathFiltersRestrictScope()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "beta\n"),
            ("src/generated/b.txt", "beta\n"),
            ("tests/c.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(
            new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { IncludeGlobs = ["src/**"], ExcludeGlobs = ["src/generated/**"], PathRegex = @"\.txt$" },
            collector);

        var actualPaths = collector.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).Path).ToArray();
        Assert.Equal(["src/a.txt"], actualPaths);
    }

    [Fact]
    public async Task GlobFiltersRespectPathSeparatorsAndDoubleStar()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "beta\n"),
            ("src/nested/b.txt", "beta\n"),
            ("src/nested/deep/c.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();

        var singleLevel = new HitBuffer();
        snapshot.Search(
            new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { IncludeGlobs = ["src/*.txt"] },
            singleLevel);
        var singleLevelPaths = singleLevel.Hits
            .Select(hit => snapshot.Materialize(hit, 0, 0).Path)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["src/a.txt"], singleLevelPaths);

        var recursive = new HitBuffer();
        snapshot.Search(
            new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { IncludeGlobs = ["src/**/*.txt"] },
            recursive);
        var recursivePaths = recursive.Hits
            .Select(hit => snapshot.Materialize(hit, 0, 0).Path)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["src/nested/b.txt", "src/nested/deep/c.txt"], recursivePaths);
    }

    [Fact]
    public async Task PathFiltersMatchBruteForceBaselineAcrossPrefixGlobAndRegex()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "beta\n"),
            ("src/generated/a.g.cs", "beta\n"),
            ("src/nested/b.txt", "beta\n"),
            ("tests/c.txt", "beta\n"),
            ("docs/readme.md", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        var filters = new[]
        {
            new PathFilter { PathPrefix = "src/" },
            new PathFilter { IncludeGlobs = ["src/**/*.txt"] },
            new PathFilter { PathRegex = @"nested/.+\.txt$" },
            new PathFilter { PathPrefix = "src/", IncludeGlobs = ["src/**/*.txt"], ExcludeGlobs = ["src/generated/**"], PathRegex = @"\.txt$" }
        };

        using var snapshot = index.OpenSnapshot();
        foreach (var filter in filters)
        {
            var collector = new HitBuffer();
            snapshot.Search(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), filter, collector);
            var actual = collector.Hits
                .Select(hit => snapshot.Materialize(hit, 0, 0).Path)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();

            var expected = fixture.Documents
                .Select(static doc => doc.Path)
                .Where(path => MatchesPathFilter(path, filter))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task PathPrefixFiltersViaSortedPathRange()
    {
        using var fixture = new CorpusFixture(
            ("docs/alpha/a.txt", "beta\n"),
            ("docs/alpha/b.txt", "beta\n"),
            ("docs/beta/c.txt", "beta\n"),
            ("src/d.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(
            new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathPrefix = "docs/alpha/" },
            collector);

        var actualPaths = collector.Hits
            .Select(hit => snapshot.Materialize(hit, 0, 0).Path)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["docs/alpha/a.txt", "docs/alpha/b.txt"], actualPaths);
        Assert.Equal(2, stats.ScopeDocuments);
    }

    [Fact]
    public async Task PathPrefixFiltersHandleUnicodePathsViaSortedRange()
    {
        using var fixture = new CorpusFixture(
            ("src/cafe.txt", "beta\n"),
            ("src/caf\u00E9.txt", "beta\n"),
            ("src/caf\u00E9/menu.txt", "beta\n"),
            ("src/cafeteria.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(
            new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathPrefix = "src/caf\u00E9" },
            collector);

        var actualPaths = collector.Hits
            .Select(hit => snapshot.Materialize(hit, 0, 0).Path)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["src/caf\u00E9.txt", "src/caf\u00E9/menu.txt"], actualPaths);
        Assert.Equal(2, stats.ScopeDocuments);
    }

    [Fact]
    public async Task PathLookupAndPrefixRangesHandleSupplementaryUnicode()
    {
        using var fixture = new CorpusFixture(
            ("src/\uE000.txt", "beta\n"),
            ("src/\U00010000.txt", "beta\n"),
            ("src/\U0010FFFF.txt", "beta\n"),
            ("tests/other.txt", "beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(
            new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathPrefix = "src/" },
            collector);
        var paths = collector.Hits
            .Select(hit => snapshot.Materialize(hit, 0, 0).Path)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "src/\U00010000.txt",
            "src/\U0010FFFF.txt",
            "src/\uE000.txt"
        ], paths);
        Assert.True(snapshot.TryGetDocumentBytes(
            new DocumentKey("src/\U00010000.txt"),
            out var content));
        Assert.Equal("beta\n", Encoding.UTF8.GetString(content.Span));
    }

    [Fact]
    public async Task TryGetDocumentBytesReturnsIndexedDocumentByCanonicalPath()
    {
        var expected = "caf\u00E9\nalpha\n";
        using var fixture = new CorpusFixture(
            ("src/caf\u00E9.txt", expected),
            ("src/other.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var found = snapshot.TryGetDocumentBytes(new DocumentKey("src\\caf\u00E9.txt"), out var content);

        Assert.True(found);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), content.ToArray());
    }

    [Fact]
    public async Task InMemoryDocumentBytesAreAViewOverTheSegmentPack()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", "alpha\n")]));

        using var snapshot = index.OpenSnapshot();
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out var content));
        Assert.True(MemoryMarshal.TryGetArray(content, out var segment));
        Assert.NotNull(segment.Array);
        Assert.True(segment.Array.Length > content.Length);
        Assert.Equal("alpha\n"u8.ToArray(), content.ToArray());
    }

    [Fact]
    public async Task TryGetDocumentBytesReturnsLatestLiveDocumentAfterUpdate()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "old\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        await index.Update(
            new InMemoryDocumentSource([
                ("src/a.txt", "new\n")
            ]),
            []);

        using var snapshot = index.OpenSnapshot();
        var found = snapshot.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out var content);

        Assert.True(found);
        Assert.Equal(Encoding.UTF8.GetBytes("new\n"), content.ToArray());
    }

    [Fact]
    public async Task OverlayTryGetDocumentBytesPrefersOverlayAndHonorsDeletes()
    {
        using var fixture = new CorpusFixture(
            ("src/keep.txt", "base keep\n"),
            ("src/delete.txt", "base delete\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenOverlaySnapshot();
        await snapshot.Apply(
            new InMemoryDocumentSource([
                ("src/keep.txt", "overlay keep\n")
            ]),
            [new DocumentKey("src/delete.txt")]);

        var keepFound = snapshot.TryGetDocumentBytes(new DocumentKey("src/keep.txt"), out var keepContent);
        var deleteFound = snapshot.TryGetDocumentBytes(new DocumentKey("src/delete.txt"), out var deleteContent);

        Assert.True(keepFound);
        Assert.Equal(Encoding.UTF8.GetBytes("overlay keep\n"), keepContent.ToArray());
        Assert.False(deleteFound);
        Assert.True(deleteContent.IsEmpty);
    }

    [Fact]
    public async Task OverlayDetachesOperationScopedBodiesBeforeApplyReturns()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([]));
        using var overlay = index.OpenOverlaySnapshot();
        var sourceContent = "alpha"u8.ToArray();
        var source = new SubmittedDocumentSource([
            new SubmittedDocument(
                "src/a.txt",
                sourceContent,
                lastModifiedUtc: null,
                contentLifetime: DocumentContentLifetime.UntilOperationCompletes)
        ]);

        await overlay.Apply(source, []);
        sourceContent.AsSpan().Fill((byte)'x');

        using var target = SiftIndex.CreateInMemory();
        await target.Build(new InMemoryDocumentSource([]));
        await overlay.ApplyTo(target);
        using var snapshot = target.OpenSnapshot();
        Assert.True(snapshot.TryGetDocumentBytes(new DocumentKey("src/a.txt"), out var content));
        Assert.Equal("alpha"u8.ToArray(), content.ToArray());
    }

    [Fact]
    public async Task ExplainUsesPathIndexForPathRegexWithMandatoryFragment()
    {
        var documents = Enumerable.Range(0, 9000)
            .Select(static i => (i % 100 == 0 ? $"src/special/{i:D5}.txt" : $"src/other/{i:D5}.txt", "beta\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(
            new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathRegex = "special/.+\\.txt$" });

        Assert.True(plan.UsesPathRegexFilter);
        Assert.True(plan.UsesPathIndex);
    }

    [Fact]
    public async Task ExplainReturnsImmutablePreparedDiagnostics()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "needle and marker\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("needle.*marker", PatternMode.Regex, CaseMode.Sensitive);

        var first = snapshot.Explain(query, new PathFilter());
        Assert.NotEmpty(first.MandatoryFragments);
        var expected = first.MandatoryFragments[0];
        var mutableView = Assert.IsAssignableFrom<IList<string>>(first.MandatoryFragments);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = "corrupted");

        var second = snapshot.Explain(query, new PathFilter());
        Assert.Equal(expected, second.MandatoryFragments[0]);
        Assert.NotSame(first.MandatoryFragments, second.MandatoryFragments);
    }

    [Fact]
    public async Task LongPathRegexRemainsSoundWithBoundedNgramSelection()
    {
        var distinguishingPath = string.Concat(Enumerable.Repeat("abcdefghij", 100));
        using var fixture = new CorpusFixture(
            ($"src/{distinguishingPath}/match.txt", "alpha"),
            ("src/unrelated/miss.txt", "alpha"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            MaxSelectedNgrams = 1
        });
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot();
        var filter = new PathFilter { PathRegex = Regex.Escape(distinguishingPath) };
        var collector = new HitBuffer();

        snapshot.Search(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive),
            filter,
            collector);

        Assert.Single(collector.Hits);
        Assert.Equal(
            $"src/{distinguishingPath}/match.txt",
            snapshot.Materialize(collector.Hits[0], 0, 0).Path);
    }

    [Fact]
    public async Task BackslashIncludeGlobUsesCanonicalPathAcceleration()
    {
        using var fixture = new CorpusFixture(
            ("src/special/a.txt", "alpha"),
            ("src/other/b.txt", "beta"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot();
        var filter = new PathFilter { IncludeGlobs = [@"src\special\*.txt"] };
        var plan = snapshot.Explain(
            new SearchQuery(string.Empty, PatternMode.Literal, CaseMode.Sensitive),
            filter);
        var collector = new HitBuffer();

        snapshot.Search(
            new SearchQuery(string.Empty, PatternMode.Literal, CaseMode.Sensitive),
            filter,
            collector);

        Assert.True(plan.UsesPathIndex);
        Assert.Single(collector.Hits);
        Assert.Equal(
            "src/special/a.txt",
            snapshot.Materialize(collector.Hits[0], 0, 0).Path);
    }

    [Fact]
    public async Task AlternativeIncludeGlobsDoNotIntersectTheirPathLiterals()
    {
        var documents = Enumerable.Range(0, 9000)
            .Select(static i => (i % 2 == 0 ? $"src/special/{i:D5}.txt" : $"docs/config/{i:D5}.txt", "beta\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(
            new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { IncludeGlobs = ["src/special/*.txt", "docs/config/*.txt"] });
        var collector = new HitBuffer();
        var stats = snapshot.Search(
            new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { IncludeGlobs = ["src/special/*.txt", "docs/config/*.txt"] },
            collector);

        Assert.True(plan.UsesIncludeGlobs);
        Assert.False(plan.UsesPathIndex);
        Assert.Equal(documents.Length, stats.HitsReturned);
    }

    [Fact]
    public async Task ExplainReportsWhenFallbackRegexCanUseNonBacktracking()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha beta\nalpha bingo\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("(?<word>b(eta|ingo))", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());

        Assert.True(plan.CanUseNonBacktracking);
        Assert.Equal(SearchVerifierKind.RegexFallback, plan.VerifierKind);
    }

    [Fact]
    public async Task SearchStatsReportFallbackRegexUsage()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha beta\nalpha bingo\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("(?<word>b(eta|ingo))", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(1, stats.RegexInvocations);
        Assert.Equal(0, stats.Utf8RegexInvocations);
        Assert.Equal(1, stats.FallbackRegexInvocations);
        Assert.Equal(1, stats.NonBacktrackingRegexInvocations);
    }

    [Fact]
    public async Task SupportedAsciiRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha beta\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("b[a-z]+a", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());
        Assert.Equal(SearchPatternKind.Utf8Regex, plan.PatternKind);
        Assert.Equal(SearchVerifierKind.Utf8Regex, plan.VerifierKind);
        Assert.False(plan.CanUseNonBacktracking);

        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("b[a-z]+a", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);
        Assert.Single(collector.Hits);
        Assert.Equal(1, stats.RegexInvocations);
        Assert.Equal(1, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
        Assert.Equal(0, stats.NonBacktrackingRegexInvocations);
        Assert.Equal(0, stats.DecodedChars);
    }

    [Fact]
    public async Task SupportedAlternationRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha beta\nalpha bingo\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("b(?:eta|ingo)", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());
        Assert.Equal(SearchVerifierKind.Utf8Regex, plan.VerifierKind);

        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("b(?:eta|ingo)", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);
        Assert.Equal(2, stats.HitsReturned);
        Assert.Equal(1, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task SupportedCapturingGroupRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha beta\nalpha bingo\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("b(eta|ingo)", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());
        Assert.Equal(SearchVerifierKind.Utf8Regex, plan.VerifierKind);

        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("b(eta|ingo)", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);
        Assert.Equal(2, stats.HitsReturned);
        Assert.Equal(1, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task SupportedAsciiInsensitiveRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha beta\n"),
            ("src/b.txt", "alpha BINGO\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("B(?:ETA|INGO)", PatternMode.Regex, CaseMode.Insensitive), new PathFilter());
        Assert.Equal(SearchVerifierKind.Utf8Regex, plan.VerifierKind);

        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("B(?:ETA|INGO)", PatternMode.Regex, CaseMode.Insensitive), new PathFilter(), collector);
        Assert.Equal(2, stats.HitsReturned);
        Assert.Equal(2, stats.RegexInvocations);
        Assert.Equal(2, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task SupportedShorthandClassRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "item 123\n"),
            ("src/b.txt", "item abc\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("\\d+", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());
        Assert.Equal(SearchVerifierKind.Utf8Regex, plan.VerifierKind);

        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("\\d+", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);
        Assert.Single(collector.Hits);
        Assert.Equal(2, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task SupportedNegatedClassRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha"),
            ("src/b.txt", "axha"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("^a[^x]+a$", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);
        Assert.Single(collector.Hits);
        Assert.Equal("src/a.txt", snapshot.Materialize(collector.Hits[0], 0, 0).Path);
        Assert.Equal(2, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task Utf8RegexEndAnchorMatchesBeforeTrailingNewline()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "alpha\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("^alph.$", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);
        Assert.Single(collector.Hits);
        Assert.Equal(1, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task SupportedWordBoundaryRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha beta gamma"),
            ("src/b.txt", "alphabetagamma"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("\\bbeta\\b", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());
        Assert.Equal(SearchVerifierKind.Utf8Regex, plan.VerifierKind);

        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("\\bbeta\\b", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);
        Assert.Single(collector.Hits);
        Assert.Equal("src/a.txt", snapshot.Materialize(collector.Hits[0], 0, 0).Path);
        Assert.Equal(2, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task DeclarationFamilyRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(
            ("src/a.cs", "public class Customer {}\npublic record Order;\npublic struct Point {}\npublic enum NoMatch { A }\n"),
            ("src/b.cs", "public interface IWidget {}\npublic classlower wrong {}\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("\\b(?:record|struct|class)\\s+[A-Z][A-Za-z0-9_]+", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new PathFilter { IncludeGlobs = ["**/*.cs"] });
        Assert.Equal(SearchVerifierKind.Utf8Regex, plan.VerifierKind);

        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter { IncludeGlobs = ["**/*.cs"] }, collector);
        Assert.Equal(3, collector.Hits.Count);
        Assert.Equal(2, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task ExactLiteralSetWordBoundaryRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "Task ValueTask IAsyncEnumerable alphabetagamma"),
            ("src/b.txt", "TaskRunner ValueTaskFactory IAsyncEnumerableBuilder"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("\\b(?:Task|ValueTask|IAsyncEnumerable)\\b", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new PathFilter());
        Assert.Equal(SearchVerifierKind.Utf8Regex, plan.VerifierKind);

        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter(), collector);
        Assert.Equal(3, collector.Hits.Count);
        Assert.All(collector.Hits, hit => Assert.Equal("src/a.txt", snapshot.Materialize(hit, 0, 0).Path));
        Assert.Equal(2, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task SupportedLazyQuantifierRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "axxxbxxb"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("a.+?b", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);
        Assert.Single(collector.Hits);
        var hit = snapshot.Materialize(collector.Hits[0], 0, 0);
        Assert.Equal("axxxb", hit.MatchText);
        Assert.Equal(1, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task AlternatingAsyncApiRegexUsesLiteralSetAnchor()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "prefix prefix prefix GetCustomerAsync suffix\n"),
            ("src/b.txt", "prefix prefix prefix TryGetOrderAsync suffix\n"),
            ("src/c.txt", "prefix prefix prefix CreateWidgetAsync suffix\n"),
            ("src/d.txt", "prefix prefix prefix SomethingElse suffix\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("\\b(?:Get|TryGet|Create)[A-Z][A-Za-z0-9]+Async\\b", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new PathFilter());
        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter(), collector);

        Assert.Equal(SearchPatternKind.Utf8Regex, plan.PatternKind);
        Assert.Equal(SearchVerifierKind.Utf8Regex, plan.VerifierKind);
        Assert.Equal(3, stats.HitsReturned);
        Assert.True(stats.Utf8RegexMatchesEnumerated < 100);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task PureAlternationRegexUsesIndexedUnionCandidates()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "this value\n"),
            ("src/b.txt", "that value\n"),
            ("src/c.txt", "foo value\n"),
            ("src/d.txt", "bar value\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("\\b(?:this|that|foo)\\b", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new PathFilter());
        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter(), collector);

        Assert.Contains(plan.PlannerAlternatives, static alternative =>
            alternative.UsesIndex && alternative.CandidateCombination == SearchCandidateCombination.Union);
        Assert.Equal(3, stats.HitsReturned);
    }

    [Fact]
    public async Task SharedPrefixAlternationRegexBuildsLeadingLiteralSet()
    {
        var query = new SearchQuery("\\[(?:Fact|Theory|InlineData)\\b", PatternMode.Regex, CaseMode.Sensitive);
        Assert.True(Utf8RegexEngine.TryCompile(query, CaseMode.Sensitive, out var program));

        var literals = program.LeadingLiteralSet!
            .Select(static bytes => System.Text.Encoding.UTF8.GetString(bytes))
            .OrderBy(static literal => literal, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["[Fact", "[InlineData", "[Theory"], literals);
    }

    [Fact]
    public async Task SharedAffixAlternationRegexBuildsLeadingLiteralSet()
    {
        var query = new SearchQuery("services\\.(?:Add|TryAdd|AddKeyed)(?:Singleton|Scoped|Transient)\\b", PatternMode.Regex, CaseMode.Sensitive);
        Assert.True(Utf8RegexEngine.TryCompile(query, CaseMode.Sensitive, out var program));

        var literals = program.LeadingLiteralSet!
            .Select(static bytes => System.Text.Encoding.UTF8.GetString(bytes))
            .OrderBy(static literal => literal, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "services.AddKeyedScoped",
            "services.AddKeyedSingleton",
            "services.AddKeyedTransient",
            "services.AddScoped",
            "services.AddSingleton",
            "services.AddTransient",
            "services.TryAddScoped",
            "services.TryAddSingleton",
            "services.TryAddTransient"
        ], literals);
    }

    [Fact]
    public async Task AsyncCallRegexBuildsOrderedAnchorGroups()
    {
        var query = new SearchQuery("\\b(?:Get|TryGet|Create|Load)[A-Z][A-Za-z0-9]+Async\\s*\\(", PatternMode.Regex, CaseMode.Sensitive);
        Assert.True(Utf8RegexEngine.TryCompile(query, CaseMode.Sensitive, out var program));

        var groups = program.OrderedAnchorGroups!
            .Select(static group => string.Join(">", group.Anchors.Select(static anchor => System.Text.Encoding.UTF8.GetString(anchor.Utf8Bytes))))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("Create>Async>(", groups);
        Assert.Contains("Get>Async>(", groups);
        Assert.Contains("Load>Async>(", groups);
        Assert.Contains("TryGet>Async>(", groups);
    }

    [Fact]
    public void OrderedAnchorGapOverflowBecomesUnbounded()
    {
        var query = new SearchQuery(
            "foo.{0,1500000000}.{0,1500000000}bar",
            PatternMode.Regex,
            CaseMode.Sensitive);

        Assert.True(Utf8RegexEngine.TryCompile(query, CaseMode.Sensitive, out var program));

        var groups = Assert.IsType<Utf8RegexOrderedAnchorGroup[]>(program.OrderedAnchorGroups);
        var group = Assert.Single(groups);
        Assert.Null(group.Anchors[1].MaxGapBytesFromPrevious);
    }

    [Fact]
    public async Task AnchorOnlyAlternationDisablesLiteralPrechecks()
    {
        var query = new SearchQuery("foo|^$", PatternMode.Regex, CaseMode.Sensitive);

        Assert.True(Utf8RegexEngine.TryCompile(query, CaseMode.Sensitive, out var program));
        Assert.Null(program.LeadingLiteralSet);
        Assert.Null(program.OrderedAnchorGroups);
    }

    [Fact]
    public async Task AsyncApiFamilyRegexEnumeratesBranchUnionPlan()
    {
        var documents = Enumerable.Range(0, 4000)
            .Select(static index => ($"src/{index:D5}.cs", (index % 5) switch
            {
                0 => $"public Task Value{index}() => GetCustomerAsync();\n",
                1 => $"public Task Value{index}() => TryGetOrderAsync();\n",
                2 => $"public Task Value{index}() => CreateWidgetAsync();\n",
                3 => $"public Task Value{index}() => GetLegacy();\n",
                _ => $"public Task Value{index}() => Value{index};\n"
            }))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("\\b(?:Get|TryGet|Create)[A-Z][A-Za-z0-9]+Async\\b", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new PathFilter { IncludeGlobs = ["**/*.cs"] });
        Assert.Contains(plan.PlannerAlternatives, static alternative =>
            alternative.UsesIndex && alternative.CandidateCombination == SearchCandidateCombination.BranchUnion);
    }

    [Fact]
    public async Task OrderedAsyncCallPrecheckReducesVerifiedDocuments()
    {
        var documents = Enumerable.Range(0, 2000)
            .Select(static index => ($"src/{index:D5}.cs", (index % 5) switch
            {
                0 => $"public Task Value{index}() => GetCustomerAsync();\n",
                1 => $"public Task Value{index}() => TryGetOrderAsync /* missing call */;\n",
                2 => $"public Task Value{index}() => LoadWidgetAsync /* missing call */;\n",
                3 => $"public Task Value{index}() => CreateWidgetAsync();\n",
                _ => $"public Task Value{index}() => LoadWidget();\n"
            }))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("\\b(?:Get|TryGet|Create|Load)[A-Z][A-Za-z0-9]+Async\\s*\\(", PatternMode.Regex, CaseMode.Sensitive);
        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter { IncludeGlobs = ["**/*.cs"] }, collector);

        Assert.True(stats.CandidateDocumentsAfterPrecheck < stats.CandidateDocumentsAfterIndex);
        Assert.Equal(800, stats.HitsReturned);
    }

    [Fact]
    public async Task OrderedAttributePrecheckUsesSameLineConstraint()
    {
        var documents = Enumerable.Range(0, 1200)
            .Select(static index => ($"src/{index:D5}.cs", (index % 4) switch
            {
                0 => "[Fact] public async Task Runs()\n",
                1 => "public [Fact] async Task WrongOrder()\n",
                2 => "[Fact]\npublic async Task SplitAcrossLines()\n",
                _ => "public async Task Plain()\n"
            }))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("\\[(?:Fact|Theory)\\][\\t ]+(?:public|internal)[\\t ]+(?:async[\\t ]+)?Task", PatternMode.Regex, CaseMode.Sensitive) { Multiline = true };
        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter { IncludeGlobs = ["**/*.cs"] }, collector);

        Assert.True(stats.CandidateDocumentsAfterPrecheck < stats.CandidateDocumentsAfterIndex);
        Assert.Equal(300, stats.HitsReturned);
    }

    [Theory]
    [InlineData("foo.{0,1}bar", "fooXX\nfoo bar")]
    [InlineData("foo[^\\r\\n]*bar", "foo\nmissing\nfoo bar")]
    public async Task ConstrainedOrderedPrecheckRetriesLaterAnchorOccurrences(
        string pattern,
        string content)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", content)]));
        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();

        var stats = snapshot.Search(
            new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive),
            new PathFilter(),
            collector);

        Assert.Equal(1, stats.HitsReturned);
        Assert.Single(collector.Hits);
    }

    [Fact]
    public async Task ConstrainedOrderedPrecheckCanRunAboveDefaultCandidateLimit()
    {
        var documents = Enumerable.Range(0, 12000)
            .Select(static index => ($"src/{index:D5}.cs", (index % 4) switch
            {
                0 => "[Fact] public async Task Runs()\n",
                1 => "public [Fact] async Task WrongOrder()\n",
                2 => "[Theory] internal async Task RunsToo()\n",
                _ => "internal [Theory] async Task WrongOrderToo()\n"
            }))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("\\b(?:public|internal)[\\t ]+\\[(?:Fact|Theory)\\]", PatternMode.Regex, CaseMode.Sensitive);
        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter { IncludeGlobs = ["**/*.cs"] }, collector);

        Assert.True(stats.CandidateDocumentsAfterIndex > 3000);
        Assert.True(stats.CandidateDocumentsAfterPrecheck < stats.CandidateDocumentsAfterIndex);
        Assert.Equal(6000, stats.HitsReturned);
    }

    [Fact]
    public async Task ServiceLifetimeRegexEnumeratesBranchUnionPlan()
    {
        var documents = Enumerable.Range(0, 3000)
            .Select(static index => ($"src/{index:D5}.cs", (index % 4) switch
            {
                0 => $"services.AddSingleton<IAlpha{index}, Alpha{index}>();\n",
                1 => $"services.AddScoped<IBeta{index}, Beta{index}>();\n",
                2 => $"services.AddTransient<IGamma{index}, Gamma{index}>();\n",
                _ => $"services.Configure<Options{index}>(config);\n"
            }))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("services\\.(?:AddSingleton|AddScoped|AddTransient)\\b", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new PathFilter { IncludeGlobs = ["**/*.cs"] });
        Assert.Contains(plan.PlannerAlternatives, static alternative =>
            alternative.UsesIndex && alternative.CandidateCombination == SearchCandidateCombination.BranchUnion);
    }

    [Fact]
    public async Task TypeDeclarationRegexUnionKeepsCandidatesBelowScope()
    {
        var documents = Enumerable.Range(0, 4000)
            .Select(static index => ($"src/{index:D5}.cs", (index % 4) switch
            {
                0 => $"public class C{index} {{ }}\n",
                1 => $"public record R{index};\n",
                2 => $"public struct S{index} {{ }}\n",
                _ => $"public enum E{index} {{ A }}\n"
            }))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("\\b(?:record|struct|class)\\s+[A-Z][A-Za-z0-9_]+", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new PathFilter { IncludeGlobs = ["**/*.cs"] });
        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter { IncludeGlobs = ["**/*.cs"] }, collector);

        Assert.True(plan.UsesIndex);
        Assert.Equal(SearchCandidateCombination.Union, plan.CandidateCombination);
        Assert.True(stats.CandidateDocumentsAfterIndex < stats.ScopeDocuments);
        Assert.Equal(3000, stats.HitsReturned);
    }

    [Fact]
    public async Task ResourceLifetimeRegexUnionKeepsCandidatesBelowScope()
    {
        var documents = Enumerable.Range(0, 3000)
            .Select(static index => ($"src/{index:D5}.cs", (index % 4) switch
            {
                0 => $"using var resource{index} = Open();\n",
                1 => $"await using var asyncResource{index} = OpenAsync();\n",
                2 => $"await foo.DisposeAsync();\n",
                _ => $"var value{index} = Open();\n"
            }))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("\\b(?:using\\s+var|await\\s+using|DisposeAsync)\\b", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new PathFilter());
        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter(), collector);

        Assert.Contains(plan.PlannerAlternatives, static alternative =>
            alternative.UsesIndex && alternative.CandidateCombination == SearchCandidateCombination.Union);
        Assert.Equal(2250, stats.HitsReturned);
    }

    [Fact]
    public async Task AlternationPlanDoesNotDropDenseAlternative()
    {
        var documents = Enumerable.Range(0, 1000)
            .Select(static index => ($"src/{index:D5}.cs", index < 900
                ? $"public class C{index} {{ }}\n"
                : $"public record R{index};\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1,
            DirectScanCandidateDensityThreshold = 0.5
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("\\b(?:class|record)\\s+[A-Z][A-Za-z0-9_]+", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new PathFilter { IncludeGlobs = ["**/*.cs"] });
        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter { IncludeGlobs = ["**/*.cs"] }, collector);

        Assert.Contains(plan.PlannerAlternatives, static alternative =>
            alternative.UsesIndex && alternative.CandidateCombination == SearchCandidateCombination.Union);
        Assert.Equal(1000, stats.HitsReturned);
    }

    [Fact]
    public async Task SupportedWholeWordRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha beta\n"),
            ("src/b.txt", "alphabet soup\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("alp.a", PatternMode.Regex, CaseMode.Sensitive) { WholeWord = true }, new PathFilter());
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("alp.a", PatternMode.Regex, CaseMode.Sensitive) { WholeWord = true }, new PathFilter(), collector);

        Assert.Equal(SearchVerifierKind.Utf8Regex, plan.VerifierKind);
        Assert.False(plan.NeedsUnicodeDecode);
        Assert.Single(collector.Hits);
        Assert.Equal(1, stats.HitsReturned);
        Assert.Equal(2, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
        Assert.Equal(1, stats.Utf8RegexCacheHits + stats.Utf8RegexCacheMisses);
    }

    [Fact]
    public async Task SupportedWholeLineRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha beta\nalpha\ngamma\n"),
            ("src/b.txt", "alpha zeta\r\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("alpha.*", PatternMode.Regex, CaseMode.Sensitive) { WholeLine = true }, new PathFilter());
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("alpha.*", PatternMode.Regex, CaseMode.Sensitive) { WholeLine = true }, new PathFilter(), collector);

        Assert.Equal(SearchVerifierKind.Utf8Regex, plan.VerifierKind);
        Assert.False(plan.NeedsUnicodeDecode);
        Assert.Equal(3, collector.Hits.Count);
        Assert.Equal(3, stats.HitsReturned);
        Assert.Equal(2, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task SupportedAsciiEscapeRegexUsesUtf8RegexEngine()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "A\u0001\u001B[\a\f\v\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("\\x41[\\cA]\\e\\[\\a\\f\\v", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("\\x41[\\cA]\\e\\[\\a\\f\\v", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(SearchVerifierKind.Utf8Regex, plan.VerifierKind);
        Assert.False(plan.NeedsUnicodeDecode);
        Assert.Single(collector.Hits);
        Assert.Equal(1, stats.HitsReturned);
        Assert.Equal(1, stats.Utf8RegexInvocations);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task SupportedAsciiRegexFallsBackPerDocumentForNonAsciiContent()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "cafe\n"),
            ("src/b.txt", "caf\u00E9\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("caf.", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(2, stats.HitsReturned);
        Assert.Equal(2, stats.RegexInvocations);
        Assert.Equal(1, stats.Utf8RegexInvocations);
        Assert.Equal(1, stats.FallbackRegexInvocations);
        Assert.Equal(
            ["src/a.txt", "src/b.txt"],
            collector.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).Path).OrderBy(static path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ExactLiteralRegexUsesByteLiteralPathInsteadOfRegexFallback()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha beta\n"),
            ("src/b.txt", "beta gamma\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("beta", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());
        Assert.Equal(SearchPatternKind.RegexLiteral, plan.PatternKind);
        Assert.Equal(SearchVerifierKind.Literal, plan.VerifierKind);
        Assert.True(plan.CanUseLiteralVerifier);

        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("beta", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);
        Assert.Equal(2, stats.HitsReturned);
        Assert.Equal(0, stats.RegexInvocations);
    }

    [Fact]
    public async Task CaseInsensitiveRegexPreservesInvariantUnicodeSemantics()
    {
        using var fixture = new CorpusFixture(
            ("src/ascii.txt", "K\n"),
            ("src/kelvin.txt", "\u212A\n"),
            ("src/final-sigma.txt", "\u03C2\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 0
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        foreach (var pattern in new[] { "K", "K+" })
        {
            var query = new SearchQuery(pattern, PatternMode.Regex, CaseMode.Insensitive);
            var plan = snapshot.Explain(query, new PathFilter());
            var collector = new HitBuffer();
            var stats = snapshot.Search(query, new PathFilter(), collector);

            Assert.False(plan.UsesIndex);
            Assert.False(plan.CanUseLiteralVerifier);
            if (pattern == "K")
            {
                Assert.Empty(plan.MandatoryFragments);
            }
            else
            {
                Assert.Contains("K", plan.MandatoryFragments);
            }
            Assert.Equal(2, stats.HitsReturned);
            Assert.Equal(
                ["src/ascii.txt", "src/kelvin.txt"],
                collector.Hits
                    .Select(hit => snapshot.Materialize(hit, 0, 0).Path)
                    .OrderBy(static path => path, StringComparer.Ordinal)
                    .ToArray());
        }

        var sigmaCollector = new HitBuffer();
        snapshot.Search(
            new SearchQuery("\u03A3", PatternMode.Regex, CaseMode.Insensitive),
            new PathFilter(),
            sigmaCollector);
        Assert.Empty(sigmaCollector.Hits);
    }

    [Fact]
    public async Task IndexedCaseInsensitiveRegexRetainsNonAsciiDocuments()
    {
        var documents = Enumerable.Range(0, 5_000)
            .Select(index => ($"src/{index:D5}.txt", $"ordinary filler number {index:D5}\n"))
            .Append(("src/ascii.txt", "KKK\n"))
            .Append(("src/kelvin.txt", "\u212A\u212A\u212A\n"))
            .ToArray();
        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("KKK+", PatternMode.Regex, CaseMode.Insensitive);
        var plan = snapshot.Explain(query, new PathFilter());
        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter(), collector);

        Assert.True(plan.UsesIndex);
        Assert.True(stats.CandidateDocumentsAfterIndex < stats.ScopeDocuments);
        Assert.Equal(
            ["src/ascii.txt", "src/kelvin.txt"],
            collector.Hits
                .Select(hit => snapshot.Materialize(hit, 0, 0).Path)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task UnicodeDenseInsensitiveRegexUsesHonestDirectPlan()
    {
        var documents = Enumerable.Range(0, 1000)
            .Select(static index => index < 900
                ? ($"src/{index:D4}.txt", $"café filler {index:D4}\n")
                : ($"src/{index:D4}.txt", $"ascii filler {index:D4}\n"))
            .ToArray();
        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("ZZZ+", PatternMode.Regex, CaseMode.Insensitive);
        var plan = snapshot.Explain(query, new PathFilter());
        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter(), collector);

        Assert.False(plan.UsesIndex);
        Assert.Equal(SearchCandidateKind.DirectScan, plan.CandidateKind);
        Assert.Equal(SearchVerifierKind.Utf8RegexWithUnicodeFallback, plan.VerifierKind);
        Assert.True(plan.NeedsUnicodeDecode);
        Assert.Equal(900, stats.FallbackRegexInvocations);
        Assert.Equal(0, stats.Utf8RegexInvocations);
        Assert.Empty(collector.Hits);
    }

    [Fact]
    public async Task RegexVerifierExposesCacheHitAndMissCounters()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha beta\n"),
            ("src/b.txt", "beta gamma\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        var pattern = $"(?<word>b(eta|ingo))(?#cache-{Guid.NewGuid():N})";
        var baselineHits = RegexVerifier.CacheHits;
        var baselineMisses = RegexVerifier.CacheMisses;

        using var snapshot = index.OpenSnapshot();
        var firstCollector = new HitBuffer();
        snapshot.Search(new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), firstCollector);
        var secondCollector = new HitBuffer();
        snapshot.Search(new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), secondCollector);

        Assert.True(RegexVerifier.CacheMisses >= baselineMisses + 1);
        Assert.True(RegexVerifier.CacheHits >= baselineHits + 1);
    }


    private static bool MatchesPathFilter(string path, in PathFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.PathPrefix) && !path.StartsWith(filter.PathPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.IncludeGlobs is { Length: > 0 })
        {
            var included = false;
            for (var i = 0; i < filter.IncludeGlobs.Length; i++)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(path, GlobToRegex(filter.IncludeGlobs[i])))
                {
                    included = true;
                    break;
                }
            }

            if (!included)
            {
                return false;
            }
        }

        if (filter.ExcludeGlobs is { Length: > 0 })
        {
            for (var i = 0; i < filter.ExcludeGlobs.Length; i++)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(path, GlobToRegex(filter.ExcludeGlobs[i])))
                {
                    return false;
                }
            }
        }

        return string.IsNullOrWhiteSpace(filter.PathRegex) || System.Text.RegularExpressions.Regex.IsMatch(path, filter.PathRegex);
    }

    private static string GlobToRegex(string glob)
    {
        var builder = "^";
        for (var i = 0; i < glob.Length; i++)
        {
            var current = glob[i];
            if (current == '*')
            {
                var isDouble = i + 1 < glob.Length && glob[i + 1] == '*';
                builder += isDouble ? ".*" : "[^/]*";
                if (isDouble)
                {
                    i++;
                }

                continue;
            }

            builder += current == '?' ? "[^/]" : System.Text.RegularExpressions.Regex.Escape(current.ToString());
        }

        return builder + "$";
    }

    [Fact]
    public async Task EmptyPatternReturnsPathMatchesWithoutScanningContent()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\n"),
            ("tests/b.txt", "beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(
            new SearchQuery(string.Empty, PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { IncludeGlobs = ["src/**"] },
            collector);

        Assert.Single(collector.Hits);
        Assert.Equal("src/a.txt", snapshot.Materialize(collector.Hits[0], 0, 0).Path);
        Assert.Equal(0, stats.BytesScanned);
    }

    [Fact]
    public async Task ByteBudgetStopsBeforeScanningAnOversizedDocument()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "0123456789"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();

        var stats = snapshot.Search(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive) { MaxBytesScanned = 5 },
            new PathFilter(),
            collector);

        Assert.Equal(SearchStopReason.MaxBytesScanned, stats.StopReason);
        Assert.Equal(0, stats.BytesScanned);
        Assert.Empty(collector.Hits);
    }

    [Fact]
    public async Task ExplainUsesIndexForLargeLiteralScope()
    {
        var documents = Enumerable.Range(0, 9000)
            .Select(static i => ($"src/{i:D5}.txt", $"alpha beta gamma {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter());

        Assert.True(plan.UsesIndex);
        Assert.NotEmpty(plan.SelectedNgrams);
        Assert.Equal(SearchCandidateKind.TrigramIndex, plan.CandidateKind);
        Assert.Equal(SearchPatternKind.Literal, plan.PatternKind);
        Assert.Equal(SearchCandidateKind.TrigramIndex, plan.CandidateKind);
        Assert.True(plan.CanUseLiteralVerifier);
        Assert.False(plan.NeedsUnicodeDecode);
    }

    [Fact]
    public async Task ExplainAggregatesDifferentSegmentStrategiesWithoutInventingAGlobalPlan()
    {
        var options = new SiftOptions
        {
            TargetSegmentDocumentCount = 1,
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 0.5
        };
        var query = new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive);

        using var matchingIndex = SiftIndex.CreateInMemory(options);
        await matchingIndex.Build(new InMemoryDocumentSource([("src/match.txt", "needle\n")]));
        using var matchingSnapshot = matchingIndex.OpenSnapshot();
        var matchingPlan = matchingSnapshot.Explain(query, new PathFilter());

        using var missingIndex = SiftIndex.CreateInMemory(options);
        await missingIndex.Build(new InMemoryDocumentSource([("src/miss.txt", "unrelated\n")]));
        using var missingSnapshot = missingIndex.OpenSnapshot();
        var missingPlan = missingSnapshot.Explain(query, new PathFilter());

        Assert.NotEqual(matchingPlan.CandidateKind, missingPlan.CandidateKind);

        using var combinedIndex = SiftIndex.CreateInMemory(options);
        await combinedIndex.Build(new InMemoryDocumentSource([
            ("src/match.txt", "needle\n"),
            ("src/miss.txt", "unrelated\n")
        ]));
        using var combinedSnapshot = combinedIndex.OpenSnapshot();
        var combinedPlan = combinedSnapshot.Explain(query, new PathFilter());

        Assert.Equal(2, combinedPlan.SegmentCount);
        Assert.Equal(SearchCandidateKind.Mixed, combinedPlan.CandidateKind);
        Assert.Equal(
            matchingPlan.EstimatedCost + missingPlan.EstimatedCost,
            combinedPlan.EstimatedCost,
            precision: 10);
        Assert.Equal(
            matchingPlan.EstimatedPathFilterCost + missingPlan.EstimatedPathFilterCost,
            combinedPlan.EstimatedPathFilterCost,
            precision: 10);
        Assert.Contains(combinedPlan.PlannerAlternatives, static alternative =>
            alternative.CandidateKind == SearchCandidateKind.DirectScan &&
            alternative.SegmentCount == 2 &&
            alternative.ScopeDocumentCount == 2);
        Assert.True(combinedPlan.RunnerUp is null || combinedPlan.RunnerUp.SegmentCount == 2);
    }

    [Fact]
    public async Task OverlayExplainPreservesDifferentBaseAndOverlayStrategies()
    {
        var options = new SiftOptions
        {
            TargetSegmentDocumentCount = 1,
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 0.5
        };
        using var index = SiftIndex.CreateInMemory(options);
        await index.Build(new InMemoryDocumentSource([("src/match.txt", "needle\n")]));
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Apply(new InMemoryDocumentSource([("src/miss.txt", "unrelated\n")]), []);

        var plan = overlay.Explain(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter());

        Assert.Equal(2, plan.SegmentCount);
        Assert.Equal(SearchCandidateKind.Mixed, plan.CandidateKind);
        Assert.True(plan.RunnerUp is null || plan.RunnerUp.SegmentCount == 2);
    }

    [Fact]
    public async Task DefaultPlannerUsesIndexForMailboxShapedSegment()
    {
        var filler = new string('x', 4096);
        var documents = Enumerable.Range(0, 300)
            .Select(i => (
                $"messages/{i:D5}.txt",
                i == 150 ? filler + " uncommon-needle\n" : filler + "\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(
            new SearchQuery("uncommon-needle", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter());

        Assert.Single(TestHelpers.GetStorage(index).ReadManifest().Segments);
        Assert.True(plan.ScopeDocumentCount < 8192);
        Assert.True(plan.UsesIndex);
        Assert.Equal(SearchCandidateKind.TrigramIndex, plan.CandidateKind);
    }

    [Fact]
    public async Task ExplainFallsBackToDirectScanForSmallLiteralScope()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha beta\n"),
            ("src/b.txt", "beta gamma\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter());

        Assert.False(plan.UsesIndex);
        Assert.Equal(SearchCandidateKind.DirectScan, plan.CandidateKind);
        Assert.Equal(SearchVerifierKind.Literal, plan.VerifierKind);
        Assert.Empty(plan.SelectedNgrams);
        Assert.False(plan.MayRequireWholeDocumentVerification);
    }

    [Fact]
    public async Task ExplainCanForceIndexedPlanForSmallScopeThroughOptions()
    {
        var largePrefix = new string('x', 8192);
        using var fixture = new CorpusFixture(
            ("src/a.txt", largePrefix + " beta\n"),
            ("src/b.txt", largePrefix + " gamma beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter());

        Assert.True(plan.UsesIndex);
        Assert.Equal(SearchCandidateKind.TrigramIndex, plan.CandidateKind);
    }

    [Fact]
    public async Task ExplainStillPrefersDirectScanWhenForcedIndexWouldNotPayOff()
    {
        var documents = Enumerable.Range(0, 10_000)
            .Select(static i => ($"src/{i:D5}.txt", "be\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);

        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("be", PatternMode.Literal, CaseMode.Sensitive), new PathFilter());

        Assert.False(plan.UsesIndex);
        Assert.Equal(SearchCandidateKind.DirectScan, plan.CandidateKind);
    }

    [Fact]
    public async Task ExplainFallsBackToDirectScanForHighDensityLiteralCandidates()
    {
        var documents = Enumerable.Range(0, 9000)
            .Select(static i => ($"src/{i:D5}.txt", $"alpha beta gamma {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter());

        Assert.False(plan.UsesIndex);
        Assert.Equal(SearchCandidateKind.DirectScan, plan.CandidateKind);
    }

    [Fact]
    public async Task ExplainCanForceIndexedPlanForHighDensityLiteralCandidates()
    {
        var documents = Enumerable.Range(0, 9000)
            .Select(static i => ($"src/{i:D5}.txt", $"alpha beta gamma {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter());

        Assert.True(plan.UsesIndex);
        Assert.Equal(SearchCandidateKind.TrigramIndex, plan.CandidateKind);
    }

    [Fact]
    public async Task ExplainChoosesBigramsForTwoBytePatternsAndTrigramsForLongerPatterns()
    {
        var documents = Enumerable.Range(0, 9000)
            .Select(static i => ($"src/{i:D5}.txt", $"alpha beta gamma {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var bigramPlan = snapshot.Explain(new SearchQuery("be", PatternMode.Literal, CaseMode.Sensitive), new PathFilter());
        var trigramPlan = snapshot.Explain(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter());

        Assert.True(bigramPlan.UsesIndex);
        Assert.Equal(SearchCandidateKind.BigramIndex, bigramPlan.CandidateKind);
        Assert.Equal(SearchCandidateKind.BigramIndex, bigramPlan.CandidateKind);
        Assert.All(bigramPlan.SelectedNgrams, static gram => Assert.Equal(2, gram.ByteLength));
        Assert.True(trigramPlan.UsesIndex);
        Assert.Equal(SearchCandidateKind.TrigramIndex, trigramPlan.CandidateKind);
        Assert.Equal(SearchCandidateKind.TrigramIndex, trigramPlan.CandidateKind);
        Assert.All(trigramPlan.SelectedNgrams, static gram => Assert.Equal(3, gram.ByteLength));
    }

    [Fact]
    public async Task ExplainStopsSelectingLiteralNgramsOnceEstimateIsSelectiveEnough()
    {
        var documents = Enumerable.Range(0, 9000)
            .Select(static i => ($"src/{i:D5}.txt", i % 1000 == 0 ? $"alpha needle gamma {i}\n" : $"alpha beta gamma {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new PathFilter());

        Assert.True(plan.UsesIndex);
        Assert.Single(plan.SelectedNgrams);
        Assert.Equal(SearchCandidateKind.TrigramIndex, plan.CandidateKind);
    }

    [Fact]
    public async Task ExplainStopsSelectingRegexFragmentNgramsOnceEstimateIsSelectiveEnough()
    {
        var documents = Enumerable.Range(0, 9000)
            .Select(static i => ($"src/{i:D5}.txt", i % 1000 == 0 ? $"alpha needle middle marker {i}\n" : $"alpha beta gamma {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("needle.*marker", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());

        Assert.NotEmpty(plan.MandatoryFragments);
        if (plan.UsesIndex)
        {
            Assert.True(plan.SelectedNgrams.Count >= 1);
        }
        else
        {
            Assert.Equal(SearchCandidateKind.DirectScan, plan.CandidateKind);
            Assert.Empty(plan.SelectedNgrams);
        }
    }

    [Fact]
    public async Task ExplainPrunesRedundantRegexMandatoryFragments()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "needle marker\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("needle", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());

        Assert.Single(plan.MandatoryFragments);
        Assert.Contains(plan.MandatoryFragments[0], "needle", StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplainFallsBackForNonAsciiInsensitiveLiteral()
    {
        var documents = Enumerable.Range(0, 9000)
            .Select(static i => ($"src/{i:D5}.txt", $"caf\u00E9 beta {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("CAF\u00C9", PatternMode.Literal, CaseMode.Insensitive), new PathFilter());

        Assert.False(plan.UsesIndex);
        Assert.Equal(SearchCandidateKind.DirectScan, plan.CandidateKind);
        Assert.Equal(SearchVerifierKind.Literal, plan.VerifierKind);
        Assert.True(plan.NeedsUnicodeDecode);
    }

    [Fact]
    public async Task ExplainRecognizesAsciiWholeLineLiteralByteVerification()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "ALPHA\nalpha beta\n"),
            ("src/b.txt", "alpha\r\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Insensitive) { WholeLine = true }, new PathFilter());

        Assert.False(plan.NeedsUnicodeDecode);
        Assert.Equal(SearchCandidateKind.DirectScan, plan.CandidateKind);
    }

    [Fact]
    public async Task AsciiInsensitiveLiteralSearchAvoidsUtf16Decode()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "Alpha BETA gamma\n"),
            ("src/b.txt", "beta beta\n"),
            ("src/c.txt", "theta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("beta", PatternMode.Literal, CaseMode.Insensitive), new PathFilter(), collector);

        Assert.Equal(3, stats.HitsReturned);
        Assert.Equal(0, stats.DecodedChars);
    }

    [Fact]
    public async Task MissingGramShortCircuitsCandidates()
    {
        var documents = Enumerable.Range(0, 9000)
            .Select(static i => ($"src/{i:D5}.txt", $"alpha beta gamma {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("zzzz", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(0, stats.HitsReturned);
        Assert.Equal(0, stats.CandidateDocumentsAfterIndex);
    }

    [Fact]
    public async Task CandidateGenerationHandlesHighDensityScopes()
    {
        var documents = Enumerable.Range(0, 9000)
            .Select(static i => ($"src/{i:D5}.txt", $"alpha beta gamma {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(9000, stats.CandidateDocumentsAfterIndex);
        Assert.Equal(9000, stats.HitsReturned);
    }

    [Fact]
    public async Task CandidateGenerationHandlesLowDensityScopes()
    {
        var documents = Enumerable.Range(0, 9000)
            .Select(static i => ($"src/{i:D5}.txt", i % 1000 == 0 ? $"alpha needle gamma {i}\n" : $"alpha beta gamma {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(9, stats.CandidateDocumentsAfterIndex);
        Assert.Equal(9, stats.HitsReturned);
    }

    [Fact]
    public async Task CandidateGenerationUsesCheckpointedLargePostingsWithoutChangingResults()
    {
        var documents = Enumerable.Range(0, 6000)
            .Select(static i => ($"src/{i:D5}.txt", i % 17 == 0 ? $"alpha needle gamma {i}\n" : $"alpha beta gamma {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new PathFilter());
        Assert.True(plan.UsesIndex);
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(documents.Count(static doc => doc.Item2.Contains("needle", StringComparison.Ordinal)), stats.CandidateDocumentsAfterIndex);
        Assert.Equal(stats.CandidateDocumentsAfterIndex, stats.HitsReturned);
    }

    [Fact]
    public async Task SegmentParallelSearchMatchesSequentialHitsAndStats()
    {
        var documents = Enumerable.Range(0, 240)
            .Select(static i => ($"src/{i:D5}.txt", i % 5 == 0 ? $"alpha needle gamma {i}\n" : $"alpha beta gamma {i}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var sequentialIndex = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 32
        });
        await sequentialIndex.Build(fixture.CreateSource());

        using var sequentialSnapshot = sequentialIndex.OpenSnapshot();
        var query = new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive) { MaxHits = int.MaxValue, MaxDocuments = int.MaxValue, MaxBytesScanned = long.MaxValue };
        var sequentialCollector = new HitBuffer();
        var sequentialStats = sequentialSnapshot.Search(query, new PathFilter(), sequentialCollector);
        var expected = sequentialCollector.Hits
            .Select(hit => sequentialSnapshot.Materialize(hit, 0, 0))
            .Select(static hit => (hit.Path, hit.StartLine, hit.StartColumn, hit.MatchText))
            .ToArray();

        using var parallelIndex = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 32
        });
        using var parallelSnapshot = parallelIndex.OpenSnapshot(new SearchOptions(4));
        var parallelCollector = new HitBuffer();
        var parallelStats = parallelSnapshot.Search(query, new PathFilter(), parallelCollector);
        var actual = parallelCollector.Hits
            .Select(hit => parallelSnapshot.Materialize(hit, 0, 0))
            .Select(static hit => (hit.Path, hit.StartLine, hit.StartColumn, hit.MatchText))
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(sequentialStats.ScopeDocuments, parallelStats.ScopeDocuments);
        Assert.Equal(sequentialStats.ScopeBytes, parallelStats.ScopeBytes);
        Assert.Equal(sequentialStats.CandidateDocumentsAfterIndex, parallelStats.CandidateDocumentsAfterIndex);
        Assert.Equal(sequentialStats.CandidateDocumentsAfterPrecheck, parallelStats.CandidateDocumentsAfterPrecheck);
        Assert.Equal(sequentialStats.VerifiedDocuments, parallelStats.VerifiedDocuments);
        Assert.Equal(sequentialStats.BytesScanned, parallelStats.BytesScanned);
        Assert.Equal(sequentialStats.DecodedChars, parallelStats.DecodedChars);
        Assert.Equal(sequentialStats.HitsReturned, parallelStats.HitsReturned);
        Assert.Equal(sequentialStats.StopReason, parallelStats.StopReason);
    }

    [Fact]
    public async Task SegmentParallelSearchPreservesManifestOrderAcrossSegments()
    {
        var documents = Enumerable.Range(0, 96)
            .Select(static i => ($"src/{i:D5}.txt", $"alpha needle {i:D5}\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 16
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot(new SearchOptions(4));
        var collector = new HitBuffer();
        snapshot.Search(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive) { MaxHits = int.MaxValue, MaxDocuments = int.MaxValue, MaxBytesScanned = long.MaxValue },
            new PathFilter(),
            collector);

        var actualPaths = collector.Hits
            .Select(hit => snapshot.Materialize(hit, 0, 0).Path)
            .ToArray();
        var expectedPaths = documents.Select(static doc => doc.Item1).ToArray();
        Assert.Equal(expectedPaths, actualPaths);
    }

    [Fact]
    public async Task SingleLaneSearchDeliversHitsWithoutBufferingTheSegment()
    {
        var documents = Enumerable.Range(0, 100)
            .Select(static i => ($"src/{i:D5}.txt", "needle\n"))
            .ToArray();
        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions());
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot(new SearchOptions(segmentParallelism: 1));
        var collector = new StopAfterFirstHitCollector();
        var stats = snapshot.Search(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive)
            {
                MaxHits = int.MaxValue,
                MaxDocuments = int.MaxValue,
                MaxBytesScanned = long.MaxValue
            },
            new PathFilter(),
            collector);

        Assert.Equal(SearchStopReason.CollectorStopped, stats.StopReason);
        Assert.Equal(1, stats.VerifiedDocuments);
        Assert.Equal(1, stats.HitsReturned);
    }

    [Theory]
    [InlineData(CaseMode.Sensitive)]
    [InlineData(CaseMode.Insensitive)]
    public async Task SegmentParallelSearchHonorsCollectorStop(CaseMode caseMode)
    {
        var repeatedMatches = string.Join(' ', Enumerable.Repeat("needle", 2_000));
        var documents = Enumerable.Range(0, 8)
            .Select(i => ($"src/{i:D2}.txt", repeatedMatches))
            .ToArray();
        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 1
        });
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot(new SearchOptions(segmentParallelism: 4));

        var stats = snapshot.Search(
            new SearchQuery("needle", PatternMode.Literal, caseMode)
            {
                MaxHits = int.MaxValue,
                MaxDocuments = int.MaxValue,
                MaxBytesScanned = long.MaxValue
            },
            new PathFilter(),
            new StopAfterFirstHitCollector());

        Assert.Equal(SearchStopReason.CollectorStopped, stats.StopReason);
        Assert.Equal(1, stats.VerifiedDocuments);
        Assert.Equal(1, stats.HitsReturned);
    }

    [Fact]
    public async Task AsciiInsensitivePrecheckObservesTheSearchDeadlineWithinADocument()
    {
        const int contentLength = 16 * 1024 * 1024;
        var pattern = new string('a', 2_047) + 'b';
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            MaxDocumentBytes = contentLength,
            DirectScanScopeBytesThreshold = long.MaxValue
        });
        await index.Build(new InMemoryDocumentSource([
            ("src/large.txt", new string('a', contentLength))
        ]));
        using var snapshot = index.OpenSnapshot();

        var stats = snapshot.Search(
            new SearchQuery(pattern, PatternMode.Literal, CaseMode.Insensitive)
            {
                MaxElapsedMilliseconds = 1,
                MaxBytesScanned = long.MaxValue
            },
            new PathFilter(),
            new HitBuffer());

        Assert.Equal(SearchStopReason.MaxElapsedTime, stats.StopReason);
    }

    [Fact]
    public async Task SegmentParallelSearchHonorsGlobalSearchLimits()
    {
        var documents = Enumerable.Range(0, 96)
            .Select(static i => ($"src/{i:D5}.txt", i % 2 == 0 ? "alpha needle\n" : "alpha beta\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var sequentialIndex = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 16
        });
        await sequentialIndex.Build(fixture.CreateSource());

        using var sequentialSnapshot = sequentialIndex.OpenSnapshot();
        var query = new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive) { MaxHits = 10, MaxDocuments = 6, MaxBytesScanned = 80 };
        var sequentialCollector = new HitBuffer();
        var sequentialStats = sequentialSnapshot.Search(query, new PathFilter(), sequentialCollector);
        var expected = sequentialCollector.Hits
            .Select(hit => sequentialSnapshot.Materialize(hit, 0, 0))
            .Select(static hit => (hit.Path, hit.StartLine, hit.StartColumn, hit.MatchText))
            .ToArray();

        using var parallelIndex = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 16
        });
        using var parallelSnapshot = parallelIndex.OpenSnapshot(new SearchOptions(4));
        var parallelCollector = new HitBuffer();
        var parallelStats = parallelSnapshot.Search(query, new PathFilter(), parallelCollector);
        var actual = parallelCollector.Hits
            .Select(hit => parallelSnapshot.Materialize(hit, 0, 0))
            .Select(static hit => (hit.Path, hit.StartLine, hit.StartColumn, hit.MatchText))
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(sequentialStats.CandidateDocumentsAfterIndex, parallelStats.CandidateDocumentsAfterIndex);
        Assert.Equal(sequentialStats.CandidateDocumentsAfterPrecheck, parallelStats.CandidateDocumentsAfterPrecheck);
        Assert.Equal(sequentialStats.VerifiedDocuments, parallelStats.VerifiedDocuments);
        Assert.Equal(sequentialStats.BytesScanned, parallelStats.BytesScanned);
        Assert.Equal(sequentialStats.DecodedChars, parallelStats.DecodedChars);
        Assert.Equal(sequentialStats.HitsReturned, parallelStats.HitsReturned);
        Assert.Equal(sequentialStats.StopReason, parallelStats.StopReason);
    }

    [Fact]
    public async Task FiniteGlobalLimitsAvoidParallelSegmentPrework()
    {
        var documents = Enumerable.Range(0, 8)
            .Select(static i => ($"src/{i:D2}.txt", "needle"))
            .ToArray();
        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 1
        });
        await index.Build(fixture.CreateSource());
        var storage = Assert.IsType<Lokad.Sift.Storage.CorpusStorage>(index.Storage);
        var opensBeforeSearch = storage.SegmentOpenCount;
        using var snapshot = index.OpenSnapshot(new SearchOptions(segmentParallelism: 4));
        var collector = new HitBuffer();

        var stats = snapshot.Search(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive) { MaxHits = 1, MaxDocuments = int.MaxValue, MaxBytesScanned = long.MaxValue },
            new PathFilter(),
            collector);

        Assert.Equal(SearchStopReason.MaxHits, stats.StopReason);
        Assert.Equal(1, storage.SegmentOpenCount - opensBeforeSearch);
    }

    [Fact]
    public async Task SnapshotReusesSegmentMappingWhenMaterializingHits()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "needle"),
            ("src/b.txt", "needle"),
            ("src/c.txt", "needle"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        var storage = Assert.IsType<Lokad.Sift.Storage.CorpusStorage>(index.Storage);
        var opensBeforeSearch = storage.SegmentOpenCount;
        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();

        snapshot.Search(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            collector);
        var materializedHits = collector.Hits
            .Select(hit => snapshot.Materialize(hit, 0, 0))
            .ToArray();

        Assert.Equal(3, materializedHits.Length);
        Assert.Equal(1, storage.SegmentOpenCount - opensBeforeSearch);
    }

    [Theory]
    [InlineData(PatternMode.Literal, "a", "a a a a a")]
    [InlineData(PatternMode.Regex, "a", "a a a a a")]
    [InlineData(PatternMode.Regex, "é", "é é é é é")]
    public async Task SearchEnforcesMaxHitsInsideOneDocument(
        PatternMode patternMode,
        string pattern,
        string content)
    {
        using var fixture = new CorpusFixture(("src/a.txt", content));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();

        var stats = snapshot.Search(
            new SearchQuery(pattern, patternMode, CaseMode.Sensitive) { MaxHits = 3 },
            new PathFilter(),
            collector);

        Assert.Equal(3, collector.Hits.Count);
        Assert.Equal(3, stats.HitsReturned);
        Assert.Equal(SearchStopReason.MaxHits, stats.StopReason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task PerDocumentHitLimitContinuesWithLaterDocuments(int segmentParallelism)
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "needle needle needle"),
            ("src/b.txt", "needle needle needle"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            TargetSegmentDocumentCount = 1
        });
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot(new SearchOptions(segmentParallelism));
        var collector = new HitBuffer();

        var stats = snapshot.Search(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive) { MaxHits = 10, MaxHitsPerDocument = 1 },
            new PathFilter(),
            collector);
        var paths = collector.Hits
            .Select(hit => snapshot.Materialize(hit, 0, 0).Path)
            .ToArray();

        Assert.Equal(["src/a.txt", "src/b.txt"], paths);
        Assert.Equal(SearchStopReason.None, stats.StopReason);
    }

    [Fact]
    public async Task MaterializeBoundsMatchAndContextText()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "1234567890\nabcdefghij\nABCDEFGHIJ\n"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            MaxMaterializedMatchBytes = 5,
            MaxMaterializedContextBytes = 4,
            MaxMaterializedContextLines = 2
        });
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(
            new SearchQuery("abcdefghij", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            collector);

        var hit = snapshot.Materialize(collector.Hits.Single(), 1, 1);

        Assert.Equal("abcde", hit.MatchText);
        Assert.Equal(["1234"], hit.BeforeContext);
        Assert.Empty(hit.AfterContext);
        Assert.True(hit.IsMatchTruncated);
        Assert.True(hit.IsContextTruncated);
    }

    [Fact]
    public async Task MaterializeDoesNotSplitUtf8ScalarsAtTheByteLimit()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "ééé"));
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            MaxMaterializedMatchBytes = 3
        });
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(
            new SearchQuery("ééé", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter(),
            collector);

        var hit = snapshot.Materialize(collector.Hits.Single(), 0, 0);

        Assert.Equal("é", hit.MatchText);
        Assert.True(hit.IsMatchTruncated);
    }

    [Fact]
    public async Task RegexSearchMatchesExpectedHits()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha beta\nbeta alpha\n"),
            ("src/b.txt", "gamma beta delta\n"),
            ("src/c.txt", "theta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("b[a-z]+a", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(3, stats.HitsReturned);
        Assert.True(stats.RegexInvocations > 0);
        Assert.True(stats.Utf8RegexInvocations > 0);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task RegexWithWhitespaceSeparatorUsesIndexedMandatoryFragments()
    {
        var documents = Enumerable.Range(0, 4000)
            .Select(static i => ($"src/{i:D5}.cs", i % 500 == 0 ? $"public async Task<int> M{i}() => {i};\n" : $"public int M{i}() => {i};\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("async\\s+Task<", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());

        Assert.True(plan.UsesIndex);
        Assert.Contains("async", plan.MandatoryFragments);
        Assert.Contains("Task<", plan.MandatoryFragments);
        Assert.Equal(SearchPatternKind.Utf8Regex, plan.PatternKind);
        Assert.NotEqual(SearchCandidateKind.DirectScan, plan.CandidateKind);

        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("async\\s+Task<", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(8, stats.HitsReturned);
        Assert.True(stats.CandidateDocumentsAfterIndex < stats.ScopeDocuments);
        Assert.True(stats.Utf8RegexInvocations > 0);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task RegexWithRequiredLiteralSuffixExpandsFragmentGrams()
    {
        var documents = Enumerable.Range(0, 4000)
            .Select(static i => ($"src/{i:D5}.cs", i % 250 == 0 ? $"public Task M{i}() => GetCustomerAsync();\n" : $"public Task M{i}() => GetCustomer();\n"))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 1,
            DirectScanScopeBytesThreshold = 1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("Get[A-Z][A-Za-z]+Async", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new PathFilter());

        Assert.True(plan.UsesIndex);
        Assert.Contains("Get", plan.MandatoryFragments);
        Assert.Contains("Async", plan.MandatoryFragments);
        Assert.Equal(SearchPatternKind.Utf8Regex, plan.PatternKind);
        Assert.Contains(plan.SelectedNgrams, static gram => gram.Text == "Asy");
        Assert.Contains(plan.SelectedNgrams, static gram => gram.Text == "syn");
        Assert.Contains(plan.SelectedNgrams, static gram => gram.Text == "ync");

        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter(), collector);

        Assert.Equal(16, stats.HitsReturned);
        Assert.True(stats.CandidateDocumentsAfterIndex < stats.ScopeDocuments);
        Assert.True(stats.Utf8RegexInvocations > 0);
        Assert.Equal(0, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task UnsupportedLookaroundRegexFallsBackSafely()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "foobar\n"),
            ("src/b.txt", "fooqux\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("foo(?=bar)", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());

        Assert.Equal(SearchPatternKind.RegexFallback, plan.PatternKind);
        Assert.Equal(SearchCandidateKind.DirectScan, plan.CandidateKind);
        Assert.Contains("foo", plan.MandatoryFragments);
        Assert.Contains("bar", plan.MandatoryFragments);
        Assert.False(plan.CanUseNonBacktracking);

        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("foo(?=bar)", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(1, stats.HitsReturned);
        Assert.Equal(["src/a.txt"], collector.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).Path).ToArray());
    }

    [Fact]
    public async Task RegexAccelerationDoesNotDiscardValidEdgeCaseMatches()
    {
        var documents = Enumerable.Range(0, 4000)
            .Select(static i => ($"src/{i:D5}.txt", i switch
            {
                100 => "foobaz\n",
                200 => "a\n",
                201 => "aa\n",
                300 => "bar\n",
                400 => "fooX\nbar\n",
                _ => $"noise-{i:D5}\n"
            }))
            .ToArray();
        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        AssertSearchContains("foo(?!bar)", "src/00100.txt");
        AssertSearchContains("a{2}", "src/00201.txt");
        AssertSearchContains("foo|^bar", "src/00300.txt");
        AssertSearchContains("foo[^\\r\\n]\\sbar", "src/00400.txt");

        void AssertSearchContains(string pattern, string expectedPath)
        {
            var collector = new HitBuffer();
            snapshot.Search(
                new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive),
                new PathFilter(),
                collector);
            var paths = collector.Hits
                .Select(hit => snapshot.Materialize(hit, 0, 0).Path)
                .ToArray();
            Assert.Contains(expectedPath, paths);
        }
    }

    [Fact]
    public async Task UnsupportedBackreferenceRegexFallsBackSafely()
    {
        var documents = Enumerable.Range(0, 4000)
            .Select(static i => ($"src/{i:D5}.txt", i switch
            {
                250 => "foofoo\n",
                1750 => "barbar\n",
                2750 => "foobar\n",
                _ => $"noise-{i:D5}\n"
            }))
            .ToArray();

        using var fixture = new CorpusFixture(documents);

        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var plan = snapshot.Explain(new SearchQuery("(foo|bar)\\1", PatternMode.Regex, CaseMode.Sensitive), new PathFilter());

        Assert.Equal(SearchPatternKind.RegexFallback, plan.PatternKind);
        Assert.Empty(plan.MandatoryFragments);
        Assert.Contains("foo", plan.AdvisorySeeds);
        Assert.Contains("bar", plan.AdvisorySeeds);
        Assert.False(plan.CanUseNonBacktracking);

        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("(foo|bar)\\1", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(2, stats.HitsReturned);
        Assert.True(stats.CandidateDocumentsAfterIndex < stats.ScopeDocuments);
        Assert.Equal(
            ["src/00250.txt", "src/01750.txt"],
            collector.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).Path).OrderBy(static path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task InlineOptionRegexUsesAdvisorySeedsForFallbackNarrowing()
    {
        var documents = Enumerable.Range(0, 4000)
            .Select(static i => ($"src/{i:D5}.txt", i switch
            {
                125 => "DEBUG = true\n",
                1125 => "debug=true\n",
                2125 => "DEBUG = false\n",
                _ => $"noise-{i:D5}\n"
            }))
            .ToArray();

        using var fixture = new CorpusFixture(documents);
        using var index = new SiftIndex(fixture.CorpusPath, new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("(?i:debug)\\s*=\\s*true", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new PathFilter());

        Assert.Equal(SearchPatternKind.RegexFallback, plan.PatternKind);
        Assert.Contains("debug", plan.AdvisorySeeds);
        Assert.Contains("true", plan.AdvisorySeeds);

        var collector = new HitBuffer();
        var stats = snapshot.Search(query, new PathFilter(), collector);

        Assert.Equal(2, stats.HitsReturned);
        Assert.True(stats.CandidateDocumentsAfterIndex < stats.ScopeDocuments);
        Assert.Equal(
            ["src/00125.txt", "src/01125.txt"],
            collector.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).Path).OrderBy(static path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task CaseInsensitiveRegexSupportsNonAsciiMatches()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "caf\u00E9\n"),
            ("src/b.txt", "CAF\u00C9\n"),
            ("src/c.txt", "cafe\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("CAF\u00C9", PatternMode.Regex, CaseMode.Insensitive), new PathFilter(), collector);

        Assert.Equal(2, stats.HitsReturned);
        Assert.Equal(
            ["src/a.txt", "src/b.txt"],
            collector.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).Path).OrderBy(static path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task UnicodeRegexPrecheckDoesNotDoubleDecodeVerifierInput()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "caf\u00E9\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("CAF\u00C9", PatternMode.Regex, CaseMode.Insensitive), new PathFilter(), collector);

        Assert.Single(collector.Hits);
        Assert.Equal("caf\u00E9\n".Length, stats.DecodedChars);
    }

    [Fact]
    public async Task UnicodeRegexMatchesReportStableByteOffsetsAcrossMultipleHits()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "caf\u00E9 caf\u00E9\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(new SearchQuery("caf\u00E9", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(2, collector.Hits.Count);
        var materialized = collector.Hits.Select(hit => snapshot.Materialize(hit, 0, 0)).ToArray();
        Assert.Equal(["caf\u00E9", "caf\u00E9"], materialized.Select(static hit => hit.MatchText).ToArray());
        Assert.Equal([1, 1], materialized.Select(static hit => hit.StartLine).ToArray());
        Assert.Equal([1, 6], materialized.Select(static hit => hit.StartColumn).ToArray());
    }

    [Fact]
    public async Task UnicodeLiteralMatchesMapOverlappingOffsetsInOnePass()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "\u00E9\u00E9\u00E9\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(new SearchQuery("\u00E9\u00E9", PatternMode.Literal, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(2, collector.Hits.Count);
        var materialized = collector.Hits.Select(hit => snapshot.Materialize(hit, 0, 0)).ToArray();
        Assert.Equal(["\u00E9\u00E9", "\u00E9\u00E9"], materialized.Select(static hit => hit.MatchText).ToArray());
        Assert.Equal([1, 2], materialized.Select(static hit => hit.StartColumn).ToArray());
    }

    [Fact]
    public async Task UnicodeRegexMatchesReportStableByteOffsetsAcrossSupplementaryCharacters()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "\uD83D\uDE00x \uD83D\uDE00x\n"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(new SearchQuery("\uD83D\uDE00x", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(2, collector.Hits.Count);
        var materialized = collector.Hits.Select(hit => snapshot.Materialize(hit, 0, 0)).ToArray();
        Assert.Equal(["\uD83D\uDE00x", "\uD83D\uDE00x"], materialized.Select(static hit => hit.MatchText).ToArray());
        Assert.Equal([1, 5], materialized.Select(static hit => hit.StartColumn).ToArray());
    }

    [Fact]
    public async Task UnicodeRegexOffsetsDoNotSplitUtf8Scalars()
    {
        using var fixture = new CorpusFixture(("src/a.txt", "\uD83D\uDE00"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(new SearchQuery(".", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(2, collector.Hits.Count);
        Assert.Equal([(0, 0), (0, 4)], collector.Hits.Select(static hit => (hit.StartByte, hit.EndByte)).ToArray());
        Assert.Equal(
            [string.Empty, "\uD83D\uDE00"],
            collector.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).MatchText).ToArray());
    }

    [Fact]
    public async Task RegexTimeoutStopsSearchSafely()
    {
        var content = new string('a', 50_000) + "b";
        using var fixture = new CorpusFixture(("src/a.txt", content));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("^(a+)+\\1$", PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), collector);

        Assert.Equal(SearchStopReason.RegexTimeout, stats.StopReason);
        Assert.Empty(collector.Hits);
    }

    [Fact]
    public async Task RegexTimeoutCanBeConfiguredToThrow()
    {
        var content = new string('a', 50_000) + "b";
        using var fixture = new CorpusFixture(("src/a.txt", content));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        Assert.Throws<RegexMatchTimeoutException>(() => snapshot.Search(
            new SearchQuery("^(a+)+\\1$", PatternMode.Regex, CaseMode.Sensitive) { TimeoutPolicy = TimeoutPolicy.Throw },
            new PathFilter(),
            collector));
    }

    [Fact]
    public async Task PathRegexTimeoutStopsSearchSafely()
    {
        var pathologicalPath = new string('a', 20_000) + "!";
        using var fixture = new CorpusFixture((pathologicalPath, "alpha"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();

        var stats = snapshot.Search(
            new SearchQuery(string.Empty, PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathRegex = "^(a+)+\\1$" },
            collector);

        Assert.Equal(SearchStopReason.RegexTimeout, stats.StopReason);
        Assert.Empty(collector.Hits);
    }

    [Fact]
    public async Task WholeWordAndWholeLineSemanticsApply()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\nalphabet\nalpha beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var wholeWord = new HitBuffer();
        snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive) { WholeWord = true }, new PathFilter(), wholeWord);
        Assert.Equal(2, wholeWord.Hits.Count);

        var wholeLine = new HitBuffer();
        snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive) { WholeLine = true }, new PathFilter(), wholeLine);
        Assert.Single(wholeLine.Hits);
    }

    [Fact]
    public async Task WholeWordTreatsSupplementaryUnicodeLettersAsWordCharacters()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "\U00010400alpha alpha\U00010400 alpha"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();

        snapshot.Search(
            new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive) { WholeWord = true },
            new PathFilter(),
            collector);

        Assert.Single(collector.Hits);
    }

    [Fact]
    public async Task WholeWordFallbackRegexUsesUnicodeScalarBoundaries()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "\U00010400alpha alpha\U00010400 alpha"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();

        var stats = snapshot.Search(
            new SearchQuery("alp.a", PatternMode.Regex, CaseMode.Sensitive) { WholeWord = true },
            new PathFilter(),
            collector);

        Assert.Single(collector.Hits);
        Assert.Equal(1, stats.FallbackRegexInvocations);
    }

    [Fact]
    public async Task AsciiWholeWordLiteralSearchAvoidsUtf16Decode()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha alphabet alpha_beta alpha.\n"),
            ("src/b.txt", "beta alpha gamma\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive) { WholeWord = true }, new PathFilter(), collector);

        Assert.Equal(3, stats.HitsReturned);
        Assert.Equal(0, stats.DecodedChars);
    }

    [Fact]
    public async Task AsciiWholeLineInsensitiveLiteralSearchAvoidsUtf16Decode()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "ALPHA\nalpha beta\n"),
            ("src/b.txt", "alpha\r\nBeta\n"),
            ("src/c.txt", "alphabet\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        var stats = snapshot.Search(new SearchQuery("alpha", PatternMode.Literal, CaseMode.Insensitive) { WholeLine = true }, new PathFilter(), collector);

        Assert.Equal(2, stats.HitsReturned);
        Assert.Equal(0, stats.DecodedChars);
    }

    [Fact]
    public async Task MultilineRegexSpansLines()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha\nbeta\n"),
            ("src/b.txt", "alpha beta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(new SearchQuery("alpha\\nbeta", PatternMode.Regex, CaseMode.Sensitive) { Multiline = true }, new PathFilter(), collector);

        Assert.Single(collector.Hits);
        var materialized = snapshot.Materialize(collector.Hits[0], 0, 0);
        Assert.Equal("src/a.txt", materialized.Path);
    }

    [Fact]
    public async Task SearchStopsAtTheWallClockBudgetBetweenDocuments()
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "needle"),
            ("src/b.txt", "needle"),
            ("src/c.txt", "needle"));
        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());
        using var snapshot = index.OpenSnapshot();
        var collector = new SlowHitCollector();

        var stats = snapshot.Search(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive) { MaxElapsedMilliseconds = 50 },
            new PathFilter(),
            collector);

        Assert.Equal(1, stats.HitsReturned);
        Assert.Equal(SearchStopReason.MaxElapsedTime, stats.StopReason);
    }

    [Fact]
    public async Task OverlaySharesOneWallClockBudgetWithTheBase()
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/base.txt", "needle")]));
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Apply(
            new InMemoryDocumentSource([("src/overlay.txt", "needle")]),
            []);
        var collector = new SlowHitCollector();

        var stats = overlay.Search(
            new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive) { MaxElapsedMilliseconds = 50 },
            new PathFilter(),
            collector);

        Assert.Equal(1, stats.HitsReturned);
        Assert.Equal(SearchStopReason.MaxElapsedTime, stats.StopReason);
    }

    private sealed class StopAfterFirstHitCollector : IHitCollector
    {
        public bool OnHit(in RawHit hit) => false;
    }

    private sealed class SlowHitCollector : IHitCollector
    {
        public bool OnHit(in RawHit hit)
        {
            Thread.Sleep(100);
            return true;
        }
    }
}




