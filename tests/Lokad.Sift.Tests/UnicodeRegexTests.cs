using System.Text;
using System.Text.RegularExpressions;

namespace Lokad.Sift.Tests;

public sealed class UnicodeRegexTests
{
    [Theory]
    [InlineData("😀|😁")]
    [InlineData("hello😀|hello😁")]
    [InlineData("😀tail|\U0001E600tail")]
    [InlineData("😀?")]
    [InlineData("😀*")]
    [InlineData("😀{0,2}")]
    [InlineData("😀+")]
    [InlineData("(?:😀)+")]
    [InlineData("😀😀")]
    [InlineData(@"\uD83D\uDE00")]
    [InlineData("(?:😀|😁)tail")]
    public async Task SupplementaryPatternsAgreeWithManagedRegex(string pattern)
    {
        await AssertContentMatches(pattern);
    }

    [Fact]
    public async Task RawSurrogatePatternsAgreeWithManagedByteProjection()
    {
        // Construct these at runtime: custom-attribute strings do not preserve
        // unpaired surrogates reliably.
        foreach (var pattern in new[] { "\uD83D", "\uDE00", "hello\uD83D", "\uDE00tail",
                     "(?:\uD83D)", @"\uD83D", @"\uDE00", "\uD83D|x" })
            await AssertContentMatches(pattern);
    }

    [Theory]
    [InlineData("hello😀|hello😁")]
    [InlineData("😀tail|\U0001E600tail")]
    [InlineData("hello😀?")]
    public async Task PathRegexMatchesAcrossOverlayUpdates(string pattern)
    {
        (string, string)[] documents = [("hello😀.txt", "needle"), ("hello😁.txt", "needle"),
            ("😀tail.txt", "needle"), ("\U0001E600tail.txt", "needle"), ("other.txt", "needle")];
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource(documents));
        foreach (var overlay in new[] { false, true })
        {
            using IIndexSnapshot snapshot = overlay ? index.OpenOverlaySnapshot() : index.OpenSnapshot();
            var visible = documents.AsEnumerable();
            if (snapshot is IOverlaySnapshot editable)
            {
                await editable.Update(new InMemoryDocumentSource([("new/hello😀tail.txt", "needle")]),
                    [new DocumentKey("hello😀.txt")]);
                visible = visible.Where(document => document.Item1 != "hello😀.txt")
                    .Append(("new/hello😀tail.txt", "needle"));
            }
            var expected = visible.Where(document => Regex.IsMatch(document.Item1, pattern,
                    RegexOptions.CultureInvariant))
                .Select(document => document.Item1).Order(StringComparer.Ordinal).ToArray();
            var hits = new HitBuffer();

            snapshot.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive),
                new PathFilter { PathRegex = pattern }, hits);

            Assert.Equal(expected, hits.Hits.Select(hit => snapshot.Materialize(hit, 0, 0).Path)
                .Order(StringComparer.Ordinal));
        }
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("éé")]
    [InlineData("😀😀")]
    public async Task SafeExactLiteralsKeepLiteralAndIndexedVerification(string pattern)
    {
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(new InMemoryDocumentSource(new[] { ("a.txt", pattern) }
            .Concat(Enumerable.Range(0, 64).Select(i => ($"noise/{i:D2}.txt", ""))).ToArray()));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive);
        var hits = new HitBuffer();

        var stats = snapshot.Search(query, new(), hits);
        var plan = snapshot.Explain(query, new());

        Assert.True(plan.CanUseLiteralVerifier);
        Assert.True(plan.UsesIndex);
        Assert.Equal((0, Encoding.UTF8.GetByteCount(pattern)),
            (Assert.Single(hits.Hits).StartByte, hits.Hits[0].EndByte));
        Assert.Equal(0, stats.RegexInvocations);
    }

    private static async Task AssertContentMatches(string pattern)
    {
        (string, string)[] texts = [("a.txt", "😀😁"), ("b.txt", "hello😀 hello😁"),
            ("c.txt", "😀tail \U0001E600tail"), ("d.txt", "😀😀x"), ("e.txt", "�tail"),
            ("empty.txt", "")];
        var documents = texts.Concat(Enumerable.Range(0, 64)
            .Select(i => ($"noise/{i:D2}.txt", ""))).ToArray();
        foreach (var favorIndex in new[] { false, true })
        foreach (var caseMode in new[] { CaseMode.Sensitive, CaseMode.Insensitive, CaseMode.Smart })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions
            {
                DirectScanScopeDocumentsThreshold = favorIndex ? 0 : 256,
                DirectScanScopeBytesThreshold = favorIndex ? 0 : 1024 * 1024,
                DirectScanCandidateDensityThreshold = favorIndex ? 1.1 : 0.85
            });
            await index.Build(new InMemoryDocumentSource(documents));
            using var snapshot = index.OpenSnapshot();
            var options = RegexOptions.CultureInvariant |
                (caseMode == CaseMode.Sensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            var expected = documents.SelectMany(document => Regex.Matches(document.Item2, pattern, options)
                .Select(match => (document.Item1, Map(document.Item2, match.Index),
                    Map(document.Item2, match.Index + match.Length)))).ToArray();
            var hits = new HitBuffer();

            snapshot.Search(new SearchQuery(pattern, PatternMode.Regex, caseMode), new(), hits);

            Assert.Equal(expected, hits.Hits.Select(hit =>
                (snapshot.Materialize(hit, 0, 0).Path, hit.StartByte, hit.EndByte)));
        }
    }

    private static int Map(string text, int offset)
    {
        // Match the established projection of positions inside a surrogate pair
        // to the beginning of that UTF-8 scalar.
        if (offset > 0 && offset < text.Length &&
            char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]))
            offset--;
        return Encoding.UTF8.GetByteCount(text.AsSpan(0, offset));
    }
}
