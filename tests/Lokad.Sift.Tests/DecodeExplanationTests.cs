namespace Lokad.Sift.Tests;

public sealed class DecodeExplanationTests
{
    [Theory]
    [InlineData("café", PatternMode.Literal, CaseMode.Sensitive)]
    [InlineData("café", PatternMode.Literal, CaseMode.Insensitive)]
    [InlineData("café", PatternMode.Literal, CaseMode.Smart)]
    [InlineData("café", PatternMode.Regex, CaseMode.Sensitive)]
    [InlineData("café", PatternMode.Regex, CaseMode.Insensitive)]
    [InlineData(@"caf\u00E9", PatternMode.Regex, CaseMode.Sensitive)]
    public async Task UnicodeLiteralExplainsActualDecoding(string pattern, PatternMode mode, CaseMode caseMode)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("a.txt", "café alpha\n")]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery(pattern, mode, caseMode);

        var stats = snapshot.Search(query, new(), new HitBuffer());
        var plan = snapshot.Explain(query, new());

        Assert.Equal(1, stats.HitsReturned);
        var decodes = caseMode != CaseMode.Sensitive;
        Assert.Equal(decodes ? 11 : 0, stats.DecodedChars);
        Assert.Equal(decodes, plan.NeedsUnicodeDecode);
        Assert.Equal(mode == PatternMode.Literal || caseMode == CaseMode.Sensitive, plan.CanUseLiteralVerifier);
    }

    [Fact]
    public async Task DecodeRequirementPrecedesCandidateAndOverlayVisibilityFiltering()
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0
        });
        await index.Build(new InMemoryDocumentSource([("a.txt", "omega\né\n")]));
        using var snapshot = index.OpenSnapshot();
        var absent = new SearchQuery("alpha[0-9]", PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(absent, new());
        var stats = snapshot.Search(absent, new(), new HitBuffer());

        Assert.True(plan.UsesIndex);
        Assert.True(plan.NeedsUnicodeDecode);
        Assert.Equal(0, stats.VerifiedDocuments);
        Assert.Equal(0, stats.DecodedChars);

        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new InMemoryDocumentSource([("a.txt", "alpha\n")]), []);
        var word = new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive) { WholeWord = true };
        Assert.True(overlay.Explain(word, new()).NeedsUnicodeDecode);
        var overlayStats = overlay.Search(word, new(), new HitBuffer());
        Assert.Equal(1, overlayStats.HitsReturned);
        Assert.Equal(0, overlayStats.DecodedChars);
    }

    [Theory]
    [InlineData(CaseMode.Sensitive, false, false, false, false)]
    [InlineData(CaseMode.Insensitive, false, true, true, false)]
    [InlineData(CaseMode.Smart, false, false, true, false)]
    [InlineData(CaseMode.Sensitive, true, false, false, false)]
    [InlineData(CaseMode.Insensitive, true, true, true, false)]
    [InlineData(CaseMode.Sensitive, true, false, false, true)]
    [InlineData(CaseMode.Insensitive, true, false, true, true)]
    [InlineData(CaseMode.Sensitive, true, true, true, true)]
    public async Task LiteralDecodeRequirementFollowsScopedInputAndBoundaryFlags(
        CaseMode caseMode, bool wholeWord, bool wholeLine, bool multiline, bool dotAll)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions { TargetSegmentDocumentCount = 1 });
        await index.Build(new InMemoryDocumentSource([("ascii/a.txt", "alpha\n"), ("unicode/b.txt", "alpha\né\n")]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery("alpha", PatternMode.Literal, caseMode)
        {
            WholeWord = wholeWord, WholeLine = wholeLine, Multiline = multiline, DotAll = dotAll
        };

        foreach (var prefix in new[] { "ascii/", "unicode/", "" })
        {
            var filter = new PathFilter { PathPrefix = prefix };
            var stats = snapshot.Search(query, filter, new HitBuffer());
            var plan = snapshot.Explain(query, filter);
            var decodes = wholeWord && !wholeLine && prefix != "ascii/";

            Assert.Equal(prefix.Length == 0 ? 2 : 1, stats.HitsReturned);
            Assert.Equal(decodes ? 8 : 0, stats.DecodedChars);
            Assert.Equal(decodes, plan.NeedsUnicodeDecode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodeRequirementTracksEmptyScopesAndOverlayInput(bool overlay)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("ascii/a.txt", "alpha\n")]));
        using var added = overlay ? index.OpenOverlaySnapshot() : null;
        if (added is not null)
            await added.Update(new InMemoryDocumentSource([("unicode/b.txt", "alpha\né\n")]), []);
        using var baseSnapshot = index.OpenSnapshot();
        IIndexSnapshot snapshot = added is null ? baseSnapshot : added;
        var wordQuery = new SearchQuery("alpha", PatternMode.Literal, CaseMode.Sensitive) { WholeWord = true };

        Assert.Equal(overlay, snapshot.Explain(wordQuery, new()).NeedsUnicodeDecode);
        Assert.Equal(overlay ? 8 : 0, snapshot.Search(wordQuery, new(), new HitBuffer()).DecodedChars);
        foreach (var query in new[] { wordQuery, new SearchQuery("café", PatternMode.Literal, CaseMode.Sensitive),
            new SearchQuery("alp.a", PatternMode.Regex, CaseMode.Sensitive),
            new SearchQuery("", PatternMode.Regex, CaseMode.Sensitive) { WholeWord = true, DotAll = true } })
        {
            var filter = new PathFilter { PathPrefix = "missing/" };
            Assert.False(snapshot.Explain(query, filter).NeedsUnicodeDecode);
            Assert.Equal(0, snapshot.Search(query, filter, new HitBuffer()).DecodedChars);
        }
    }

    [Fact]
    public async Task NativeAndManagedDecodeExplanationsFollowPathScope()
    {
        foreach (var native in new[] { false, true })
        {
            using var engine = new RegexContractTestEngine();
            using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = native ? engine : null });
            await index.Build(new InMemoryDocumentSource([("ascii/a.txt", "alpha1"), ("unicode/b.txt", "alphaé")]));
            using var snapshot = index.OpenSnapshot();
            var query = new SearchQuery("alpha[0-9]*", PatternMode.Regex, CaseMode.Sensitive);
            foreach (var prefix in new[] { "ascii/", "unicode/", "" })
            {
                var filter = new PathFilter { PathPrefix = prefix };
                var stats = snapshot.Search(query, filter, new HitBuffer());

                Assert.Equal(!native || prefix != "ascii/", snapshot.Explain(query, filter).NeedsUnicodeDecode);
                Assert.Equal(!native || prefix != "ascii/", stats.DecodedChars > 0);
                Assert.Equal(prefix.Length == 0 ? 2 : 1, stats.HitsReturned);
            }
        }
    }
}
