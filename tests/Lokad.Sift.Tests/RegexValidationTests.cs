using System.Text;
using System.Text.RegularExpressions;

namespace Lokad.Sift.Tests;

public sealed class RegexValidationTests
{
    [Theory]
    [InlineData("(?<1a>foo)")]
    [InlineData("(?<0>foo)")]
    [InlineData("(?<x!>foo)")]
    [InlineData("(?'x!'foo)")]
    [InlineData("(?<X!>foo)")]
    [InlineData("(?:(?<1a>foo))")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("?")]
    [InlineData("?foo")]
    [InlineData("{1}")]
    [InlineData("foo{1,1}+")]
    public async Task InvalidSyntaxIsRejectedBeforePathFiltering(string pattern)
    {
        Assert.Throws<RegexParseException>(() => new Regex(pattern, RegexOptions.CultureInvariant));
        foreach (var configureEngine in new[] { false, true })
        {
            using var engine = new RegexContractTestEngine { FailCompile = true };
            using var index = SiftIndex.CreateInMemory(new SiftOptions { RegexEngine = configureEngine ? engine : null });
            await index.Build(new InMemoryDocumentSource([("a.txt", "foo")]));
            using var snapshot = index.OpenSnapshot();
            foreach (var caseMode in new[] { CaseMode.Sensitive, CaseMode.Insensitive, CaseMode.Smart })
            foreach (var filter in new[] { new PathFilter(), new PathFilter { PathPrefix = "missing/" } })
            {
                var query = new SearchQuery(pattern, PatternMode.Regex, caseMode);
                var hits = new HitBuffer();

                Assert.Throws<RegexParseException>(() => snapshot.Search(query, filter, hits));
                Assert.Throws<RegexParseException>(() => snapshot.Explain(query, filter));
                Assert.Empty(hits.Hits);
            }
            Assert.Equal(0, engine.Compilations);
        }
    }

    [Theory]
    [InlineData("(?<1a>foo)")]
    [InlineData("?foo")]
    public async Task InvalidSyntaxDoesNotDependOnCorpusOrPoisonTheQueryCache(string pattern)
    {
        foreach (var empty in new[] { false, true })
        foreach (var overlay in new[] { false, true })
        {
            using var index = SiftIndex.CreateInMemory();
            await index.Build(new InMemoryDocumentSource(empty ? [] : [("a.txt", "foo")]));
            using IIndexSnapshot snapshot = overlay ? index.OpenOverlaySnapshot() : index.OpenSnapshot();
            var invalid = new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.Throws<RegexParseException>(() => snapshot.Explain(invalid, new()));
                Assert.Throws<RegexParseException>(() => snapshot.Search(invalid, new(), new HitBuffer()));
            }

            var valid = snapshot.Search(new SearchQuery("foo", PatternMode.Regex, CaseMode.Sensitive),
                new(), new HitBuffer());

            Assert.Equal(empty ? 0 : 1, valid.HitsReturned);
        }
    }

    [Theory]
    [InlineData("(?<valid>foo)", false)]
    [InlineData("(?'valid'foo)", false)]
    [InlineData("(?<1>foo)", false)]
    [InlineData("(?<é>foo)", false)]
    [InlineData("(?:(?<valid>foo))", false)]
    [InlineData("(?<valid>foo)bar", false)]
    [InlineData("pre(?<valid>foo)post", false)]
    [InlineData("foo", true)]
    [InlineData("(?:foo)", true)]
    [InlineData("(foo)", true)]
    [InlineData(@"\{literal\}", true)]
    [InlineData("{literal}", false)]
    public async Task ValidGroupsRetainEnumerationAndSoundIndexing(string pattern, bool literalShortcut)
    {
        (string, string)[] documents = [("a.txt", "foofoo foo"), ("b.txt", "foobar"),
            ("c.txt", "prefoopost"), ("d.txt", "éfoo 😀foo FOO"), ("e.txt", "{literal}"), ("empty.txt", "")];
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(new InMemoryDocumentSource(documents.Concat(Enumerable.Range(0, 64)
            .Select(i => ($"noise/{i:D2}.txt", new string('q', 100)))).ToArray()));
        using var snapshot = index.OpenSnapshot();
        foreach (var caseMode in new[] { CaseMode.Sensitive, CaseMode.Insensitive, CaseMode.Smart })
        {
            var query = new SearchQuery(pattern, PatternMode.Regex, caseMode);
            var options = RegexOptions.CultureInvariant |
                (caseMode == CaseMode.Sensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            var expected = documents.SelectMany(document => Regex.Matches(document.Item2, pattern, options)
                .Select(match => (document.Item1,
                    Encoding.UTF8.GetByteCount(document.Item2.AsSpan(0, match.Index)),
                    Encoding.UTF8.GetByteCount(document.Item2.AsSpan(0, match.Index + match.Length))))).ToArray();
            var hits = new HitBuffer();

            snapshot.Search(query, new(), hits);
            var plan = snapshot.Explain(query, new());

            Assert.Equal(expected, hits.Hits.Select(hit =>
                (snapshot.Materialize(hit, 0, 0).Path, hit.StartByte, hit.EndByte)));
            Assert.Equal(caseMode == CaseMode.Sensitive && literalShortcut, plan.CanUseLiteralVerifier);
            if (caseMode == CaseMode.Sensitive && pattern != "{literal}")
                Assert.True(plan.UsesIndex);
        }
    }
}
