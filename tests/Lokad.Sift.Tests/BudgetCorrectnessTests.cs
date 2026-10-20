using System.Text.RegularExpressions;
using Lokad.Sift;

namespace Lokad.Sift.Tests;

public sealed class BudgetCorrectnessTests
{
    [Theory]
    [InlineData("abc|xyz")]
    [InlineData("xyz|abc")]
    [InlineData("abc|xyz|mno")]
    [InlineData("abc|xyz|mno|def")]
    [InlineData("(?:abc|xyz|mno)TAIL")]
    [InlineData("abc.*TAIL")]
    [InlineData("abc|de")]
    [InlineData("abc|x")]
    [InlineData("abc|notpresent")]
    [InlineData("abcfoo|abcbar")]
    [InlineData("abc(?=$)|xyz(?=$)")]
    [InlineData("(?i:abc)|(?i:xyz)")]
    [InlineData("(?=abc)abc|(?=xyz)xyz")]
    [InlineData("aaa|bbb|ccc|ddd|eee|fff|ggg|hhh|iii|jjj")]
    [InlineData("aaa(?=$)|bbb(?=$)|ccc(?=$)|ddd(?=$)|eee(?=$)|fff(?=$)|ggg(?=$)|hhh(?=$)|iii(?=$)|jjj(?=$)")]
    public async Task NgramBudgetsDoNotChangeRegexResults(string pattern)
    {
        string[] texts = ["abc", "xyz", "mno", "def", "abcTAIL", "xyzTAIL", "mnoTAIL", "abcTAILxyz",
            "de", "x", "abcfoo", "abcbar", "ABC", "XYZ", "aaa", "bbb", "ccc", "ddd", "eee", "fff",
            "ggg", "hhh", "iii", "jjj"];
        var documents = texts.Select((text, i) => ($"{i:D3}.txt", text))
            .Concat(Enumerable.Range(texts.Length, 64 - texts.Length)
                .Select(i => ($"{i:D3}.txt", new string('q', 4096)))).ToArray();
        foreach (var favorIndex in new[] { false, true })
        foreach (var caseMode in new[] { CaseMode.Sensitive, CaseMode.Insensitive })
        foreach (var budget in new[] { 1, 2, 3, 4, 8, 24, SiftOptions.MaximumSelectedNgramCount })
        {
            using var index = SiftIndex.CreateInMemory(new SiftOptions
            {
                MaxSelectedNgrams = budget,
                DirectScanScopeDocumentsThreshold = favorIndex ? 0 : 256,
                DirectScanScopeBytesThreshold = favorIndex ? 0 : 1024 * 1024,
                DirectScanCandidateDensityThreshold = favorIndex ? 1.1 : 0.85
            });
            await index.Build(new InMemoryDocumentSource(documents));
            using var snapshot = index.OpenSnapshot();
            var options = RegexOptions.CultureInvariant | RegexOptions.Multiline |
                (caseMode == CaseMode.Insensitive ? RegexOptions.IgnoreCase : RegexOptions.None);
            var expected = documents.SelectMany(document => Regex.Matches(document.Item2, pattern, options)
                .Select(match => (document.Item1, match.Index, match.Index + match.Length))).ToArray();
            var query = new SearchQuery(pattern, PatternMode.Regex, caseMode);
            var hits = new HitBuffer();
            var plan = snapshot.Explain(query, new PathFilter());

            snapshot.Search(query, new PathFilter(), hits);

            var actual = hits.Hits.Select(hit => (snapshot.Materialize(hit, 0, 0).Path, hit.StartByte, hit.EndByte)).ToArray();
            Assert.True(expected.SequenceEqual(actual),
                $"Pattern={pattern}, budget={budget}, case={caseMode}, favorIndex={favorIndex}: expected {expected.Length} hits, got {actual.Length}.");
            Assert.True(plan.SelectedNgrams.Count <= budget);
        }
    }

    [Theory]
    [InlineData("aaa(?=$)|bbb(?=$)|ccc(?=$)|ddd(?=$)|eee(?=$)|fff(?=$)|ggg(?=$)|hhh(?=$)|iii(?=$)|jjj(?=$)", false)]
    [InlineData("abc(?=$)|xyz(?=$)", true)]
    [InlineData("abc(?=$)|x(?=$)", false)]
    public async Task AdvisorySeedSelectionCannotExcludeRegexBranches(string pattern, bool denseBranch)
    {
        string[] seeds = ["abc", "xyz", "x", "aaa", "bbb", "ccc", "ddd", "eee", "fff", "ggg", "hhh", "iii", "jjj"];
        var documents = Enumerable.Range(0, 64).Select(i => ($"{i:D3}.txt",
            i < seeds.Length ? seeds[i] : denseBranch && i < 50 ? "abc" : new string('q', 4096))).ToArray();
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(new InMemoryDocumentSource(documents));
        using var snapshot = index.OpenSnapshot();
        var expected = documents.SelectMany(document => Regex.Matches(document.Item2, pattern)
            .Select(match => (document.Item1, match.Index, match.Index + match.Length))).ToArray();
        var hits = new HitBuffer();

        snapshot.Search(new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), hits);

        Assert.Equal(expected, hits.Hits.Select(hit => (snapshot.Materialize(hit, 0, 0).Path, hit.StartByte, hit.EndByte)));
    }

    [Theory]
    [InlineData("abc|xyz", 1)]
    [InlineData("abc(?=$)|xyz(?=$)", 24)]
    public async Task OverlayAndBaseRetainAllRegexBranches(string pattern, int budget)
    {
        var documents = Enumerable.Range(0, 64)
            .Select(i => ($"{i:D3}.txt", i < 2 ? "abc" : new string('q', 4096))).ToArray();
        using var index = SiftIndex.CreateInMemory(new SiftOptions
        {
            MaxSelectedNgrams = budget,
            DirectScanScopeDocumentsThreshold = 0,
            DirectScanScopeBytesThreshold = 0,
            DirectScanCandidateDensityThreshold = 1.1
        });
        await index.Build(new InMemoryDocumentSource(documents));
        using var overlay = index.OpenOverlaySnapshot();
        await overlay.Update(new InMemoryDocumentSource([("000.txt", "xyz"), ("new.txt", "xyz")]), []);
        var hits = new HitBuffer();

        overlay.Search(new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive), new PathFilter(), hits);

        var actual = hits.Hits.Select(hit => (overlay.Materialize(hit, 0, 0).Path, hit.StartByte, hit.EndByte))
            .OrderBy(hit => hit.Path, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { ("000.txt", 0, 3), ("001.txt", 0, 3), ("new.txt", 0, 3) }, actual);
    }
}
