using System.Text;
using System.Text.RegularExpressions;
using Lokad.Sift;

namespace Lokad.Sift.Tests;

public sealed class ConservativeRegexAnalysisTests
{
    [Theory]
    [InlineData("(?x:foo bar)")]
    [InlineData("(?X:foo bar)")]
    [InlineData("(?x:foo# comment\nbar)")]
    [InlineData("(?x:foo\tbar )")]
    [InlineData("pre(?x:foo bar)post")]
    [InlineData("(?x:foo(?-x: )bar)")]
    [InlineData("(?x:(?i:foo) bar)")]
    [InlineData("(?x-i:foo bar)")]
    [InlineData("(?iX:foo bar)")]
    [InlineData("(?I:foo)bar")]
    [InlineData("(?n:foo)bar")]
    [InlineData("(?s:foo.bar)")]
    [InlineData("[]a]foobar")]
    [InlineData("[^]a]foobar")]
    [InlineData("(?=)[]a]foobar")]
    [InlineData("(?<=x)[]a]foobar")]
    [InlineData("(?=)[^]a]foobar")]
    [InlineData("(?:[]a]|x)foobar")]
    [InlineData(@"(?=)[\]a]foobar")]
    [InlineData("(?=)[]]foobar")]
    [InlineData("(?=)[^]]foobar")]
    public async Task ContentResultsAgreeWithManagedRegex(string pattern)
    {
        string[] texts = ["foobar", "foo bar", "FOOBAR", "Foobar", "foo# comment\nbar", "foo\nbar",
            "prefoobarpost", "preFOObarpost", "]foobar", "afoobar", "bfoobar", "x]foobar",
            "xafoobar", "ébfoobar", "éfoobar", "foo barpost", "FOObar"];
        var documents = texts.Select((text, i) => ($"{i:D3}.txt", text))
            .Concat(Enumerable.Range(texts.Length, 64 - texts.Length)
                .Select(i => ($"{i:D3}.txt", new string('q', 4096)))).ToArray();
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
            var ignoreCase = caseMode == CaseMode.Insensitive ||
                caseMode == CaseMode.Smart && !pattern.Any(char.IsUpper);
            var options = RegexOptions.CultureInvariant |
                (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            var expected = documents.SelectMany(document => Regex.Matches(document.Item2, pattern, options)
                .Select(match => (document.Item1,
                    Encoding.UTF8.GetByteCount(document.Item2.AsSpan(0, match.Index)),
                    Encoding.UTF8.GetByteCount(document.Item2.AsSpan(0, match.Index + match.Length))))).ToArray();
            var hits = new HitBuffer();

            snapshot.Search(new SearchQuery(pattern, PatternMode.Regex, caseMode), new PathFilter(), hits);

            Assert.Equal(expected, hits.Hits.Select(hit =>
                (snapshot.Materialize(hit, 0, 0).Path, hit.StartByte, hit.EndByte)));
        }
    }

    [Theory]
    [InlineData("[]a]foobar")]
    [InlineData("[^]a]foobar")]
    [InlineData("(?=)[]a]foobar")]
    [InlineData(@"[\]a]foobar")]
    [InlineData("(?x:foo bar)")]
    [InlineData("(?X:foo# comment\nbar)")]
    public async Task PathResultsAgreeWithManagedRegexAcrossOverlayUpdates(string pattern)
    {
        (string, string)[] documents = [("afoobar.txt", "needle"), ("]foobar.txt", "needle"),
            ("bfoobar.txt", "needle"), ("foobar.txt", "needle"), ("foo bar.txt", "needle"),
            ("éfoobar.txt", "needle")];
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource(documents));
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new InMemoryDocumentSource([("new/afoobar.txt", "needle")]),
            [new DocumentKey("afoobar.txt")]);
        var expected = documents.Where(document => document.Item1 != "afoobar.txt")
            .Append(("new/afoobar.txt", "needle"))
            .Where(document => Regex.IsMatch(document.Item1, pattern, RegexOptions.CultureInvariant))
            .Select(document => document.Item1).Order(StringComparer.Ordinal).ToArray();
        var hits = new HitBuffer();

        overlay.Search(new SearchQuery("needle", PatternMode.Literal, CaseMode.Sensitive),
            new PathFilter { PathRegex = pattern }, hits);

        Assert.Equal(expected, hits.Hits.Select(hit => overlay.Materialize(hit, 0, 0).Path)
            .Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("(?x:foo bar)")]
    [InlineData("(?I:foo)bar")]
    [InlineData("(?=)[]a]foobar")]
    public async Task OverlayContentResultsAgreeWithManagedRegex(string pattern)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("base.txt", "foobar"), ("replace.txt", "foo bar")]));
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new InMemoryDocumentSource([("replace.txt", "FOObar"), ("new.txt", "é]foobar")]), []);
        (string, string)[] visible = [("base.txt", "foobar"), ("replace.txt", "FOObar"), ("new.txt", "é]foobar")];
        var expected = visible.SelectMany(document => Regex.Matches(document.Item2, pattern, RegexOptions.CultureInvariant)
            .Select(match => (document.Item1,
                Encoding.UTF8.GetByteCount(document.Item2.AsSpan(0, match.Index)),
                Encoding.UTF8.GetByteCount(document.Item2.AsSpan(0, match.Index + match.Length)))))
            .OrderBy(hit => hit.Item1, StringComparer.Ordinal).ToArray();
        var hits = new HitBuffer();

        overlay.Search(new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), hits);

        Assert.Equal(expected, hits.Hits.Select(hit =>
            (overlay.Materialize(hit, 0, 0).Path, hit.StartByte, hit.EndByte))
            .OrderBy(hit => hit.Path, StringComparer.Ordinal));
    }
}
