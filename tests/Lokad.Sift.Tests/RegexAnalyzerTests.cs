using System.Text.RegularExpressions;
using Lokad.Sift.Search;

namespace Lokad.Sift.Tests;

public sealed class RegexAnalyzerTests
{
    [Theory]
    [InlineData("(?(1)foo|bar)")]
    [InlineData("(?i:foo)bar")]
    [InlineData("(?-i:foo)bar")]
    [InlineData("(?<open>foo)(?<-open>bar)")]
    [InlineData("(?(name)foo|bar)")]
    [InlineData("a(?im-s:bc)d")]
    public async Task AnalyzerGivesUpOnUnsupportedConstructs(string pattern)
    {
        Assert.Empty(RegexFragmentAnalyzer.ExtractMandatoryFragments(pattern));
    }

    [Fact]
    public async Task BackreferenceMandatoryFragmentsRemainSound()
    {
        Assert.Contains("foo", RegexFragmentAnalyzer.ExtractMandatoryFragments("(foo)\\1"));
    }

    [Fact]
    public async Task AdvisorySeedsSurviveUnsupportedFallbackConstructs()
    {
        Assert.Contains("foo", RegexFragmentAnalyzer.AnalyzeFallbackPattern("(foo|bar)\\1").AdvisorySeeds);
        Assert.Contains("bar", RegexFragmentAnalyzer.AnalyzeFallbackPattern("(foo|bar)\\1").AdvisorySeeds);
        Assert.Contains("public", RegexFragmentAnalyzer.AnalyzeFallbackPattern("public(?!\\s+static)").AdvisorySeeds);
        Assert.Contains("struct", RegexFragmentAnalyzer.AnalyzeFallbackPattern("(?<!readonly\\s)struct").AdvisorySeeds);
    }

    [Fact]
    public async Task AnalyzerFragmentsRemainSoundAcrossGeneratedPatterns()
    {
        foreach (var pattern in GetSoundnessPatterns())
        {
            AssertFragmentsSound(pattern, EnumerateTexts("ab", 4));
        }
    }

    [Fact]
    public async Task AnalyzerExtractsExpectedFragmentsForSimpleSupportedPatterns()
    {
        Assert.Contains("foobar", RegexFragmentAnalyzer.ExtractMandatoryFragments("foobar"));
        Assert.Contains(RegexFragmentAnalyzer.ExtractMandatoryFragments("foo(bar)?"), static fragment => fragment.Contains("foo", StringComparison.Ordinal));
        Assert.Contains("baz", RegexFragmentAnalyzer.ExtractMandatoryFragments("(foo|bar)baz"));
        Assert.Contains(RegexFragmentAnalyzer.ExtractMandatoryFragments("(?<name>foo)bar"), static fragment => fragment.Contains("foo", StringComparison.Ordinal));
        Assert.Contains(RegexFragmentAnalyzer.ExtractMandatoryFragments("(?'name'foo)bar"), static fragment => fragment.Contains("foo", StringComparison.Ordinal));
        Assert.Contains(RegexFragmentAnalyzer.ExtractMandatoryFragments("foo(?#comment)bar"), static fragment => fragment.Contains("foo", StringComparison.Ordinal));
        Assert.Contains("AB", RegexFragmentAnalyzer.ExtractMandatoryFragments("\\x41\\u0042"));
        Assert.Contains("\a\f\v", RegexFragmentAnalyzer.ExtractMandatoryFragments("\\a\\f\\v"));
        Assert.Contains($"\u001B[", RegexFragmentAnalyzer.ExtractMandatoryFragments("\\e\\["));
        Assert.Contains("\u0001\u001A", RegexFragmentAnalyzer.ExtractMandatoryFragments("\\cA\\cZ"));
        Assert.Contains("async", RegexFragmentAnalyzer.ExtractMandatoryFragments("async\\s+Task<"));
        Assert.Contains("Task<", RegexFragmentAnalyzer.ExtractMandatoryFragments("async\\s+Task<"));
        Assert.Contains("foo", RegexFragmentAnalyzer.ExtractMandatoryFragments("foo(?=bar)"));
        Assert.Contains("bar", RegexFragmentAnalyzer.ExtractMandatoryFragments("foo(?=bar)"));
        Assert.Contains("foo", RegexFragmentAnalyzer.ExtractMandatoryFragments("(?<=foo)bar"));
        Assert.Contains("bar", RegexFragmentAnalyzer.ExtractMandatoryFragments("(?<=foo)bar"));
        Assert.Contains("foobar", RegexFragmentAnalyzer.ExtractMandatoryFragments("foo(?>bar(?=baz))"));
        Assert.Contains("baz", RegexFragmentAnalyzer.ExtractMandatoryFragments("foo(?>bar(?=baz))"));
        Assert.Contains("Get", RegexFragmentAnalyzer.ExtractMandatoryFragments("Get[A-Z][A-Za-z]+Async"));
        Assert.Contains("Async", RegexFragmentAnalyzer.ExtractMandatoryFragments("Get[A-Z][A-Za-z]+Async"));
        Assert.Contains("Async", RegexFragmentAnalyzer.ExtractMandatoryFragments("\\b(?:Get|TryGet|Create)[A-Z][A-Za-z0-9]+Async\\b"));
        Assert.Contains("Import", RegexFragmentAnalyzer.ExtractMandatoryFragments("\\[(?:DllImport|LibraryImport)"));
        Assert.Contains("Disposable", RegexFragmentAnalyzer.ExtractMandatoryFragments("(?:IDisposable|IAsyncDisposable)"));
    }

    [Theory]
    [InlineData("^a[^x]+a$", "abca\naaa", false)]
    [InlineData("\\bbeta\\b", "alphabetabeta beta", false)]
    [InlineData("a+", "aaa a", false)]
    [InlineData("a{2}", "aaa a", false)]
    [InlineData("a{1,2}", "aaa a", false)]
    [InlineData("a{1}", "aaa a", true)]
    public async Task RegexLiteralClassificationPreservesBoundariesAndRepetition(
        string pattern, string text, bool usesLiteralVerifier)
    {
        using var index = SiftIndex.CreateInMemory();
        await index.Build(new InMemoryDocumentSource([("src/a.txt", text)]));
        using var snapshot = index.OpenSnapshot();
        var query = new SearchQuery(pattern, PatternMode.Regex, CaseMode.Sensitive);
        var plan = snapshot.Explain(query, new PathFilter());
        var hits = new HitBuffer();
        snapshot.Search(query, new PathFilter(), hits);

        Assert.Equal(usesLiteralVerifier, plan.CanUseLiteralVerifier);
        var expected = System.Text.RegularExpressions.Regex.Matches(text, pattern)
            .Select(static match => (match.Index, match.Index + match.Length));
        Assert.Equal(expected, hits.Hits.Select(static hit => (hit.StartByte, hit.EndByte)));
    }

    [Fact]
    public void AnalyzerTreatsOversizedQuantifiersAndSurrogatesAsUnsupported()
    {
        const string oversized = "a{999999999999999999999999999999}";

        Assert.Empty(RegexFragmentAnalyzer.ExtractMandatoryFragments(oversized));
        Assert.Empty(RegexFragmentAnalyzer.ExtractMandatoryFragments("\\uD800"));
    }

    [Fact]
    public async Task NegativeLookaroundsDoNotContributeMandatoryFragments()
    {
        var lookahead = RegexFragmentAnalyzer.ExtractMandatoryFragments("foo(?!bar)");
        var lookbehind = RegexFragmentAnalyzer.ExtractMandatoryFragments("(?<!readonly\\s)struct");

        Assert.Contains("foo", lookahead);
        Assert.DoesNotContain("bar", lookahead);
        Assert.Contains("struct", lookbehind);
        Assert.DoesNotContain("readonly", lookbehind);
    }

    [Fact]
    public async Task AnalyzerFragmentsRemainSoundAcrossGeneratedSequencesAndAlternations()
    {
        foreach (var pattern in GenerateSupportedPatterns())
        {
            AssertFragmentsSound(pattern, EnumerateTexts("ab.\n\t", 5));
        }
    }

    private static IEnumerable<string> GetSoundnessPatterns()
    {
        return
        [
            "a",
            "b",
            "ab",
            "ba",
            "aba",
            "a?",
            "b+",
            "ab+",
            "a{1,2}",
            "ab{1,2}",
            "a(b|c)",
            "(ab|ac)",
            "(a|b)c",
            "(a|b)c+",
            "a[bc]d",
            "(?:ab)c",
            "(?>ab)c",
            "ab|ac",
            "(ab|ac)d",
            "a\\.b",
            "a\\+b",
            "a\\tb",
            "a\\nb",
            "a.*b",
            "a.?b"
        ];
    }

    private static IEnumerable<string> GenerateSupportedPatterns()
    {
        var atoms =
            new[]
            {
                "a",
                "b",
                ".",
                "\\.",
                "\\+",
                "\\n",
                "\\t",
                "[a]",
                "[b]",
                "(?:a)",
                "(?:ab)",
                "(?>a)",
                "(?>ab)"
            };
        var quantifiers = new[] { string.Empty, "?", "+", "{1,2}" };
        var sequences = new HashSet<string>(StringComparer.Ordinal);

        foreach (var atom in atoms)
        {
            foreach (var quantifier in quantifiers)
            {
                sequences.Add(atom + quantifier);
            }
        }

        foreach (var left in atoms)
        {
            foreach (var right in atoms)
            {
                sequences.Add(left + right);
                sequences.Add(left + right + "?");
                sequences.Add(left + right + "+");
                sequences.Add(left + right + "{1,2}");
            }
        }

        foreach (var left in sequences.ToArray())
        {
            yield return left;
        }

        foreach (var left in atoms)
        {
            foreach (var right in atoms)
            {
                yield return left + "|" + right;
                yield return "(?:" + left + "|" + right + ")a";
                yield return "(?>" + left + "|" + right + ")a";
            }
        }
    }

    private static void AssertFragmentsSound(string pattern, IEnumerable<string> texts)
    {
        var fragments = RegexFragmentAnalyzer.ExtractMandatoryFragments(pattern);
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        foreach (var text in texts)
        {
            if (!regex.IsMatch(text))
            {
                continue;
            }

            foreach (var fragment in fragments)
            {
                Assert.True(
                    text.Contains(fragment, StringComparison.Ordinal),
                    $"Pattern '{pattern}' extracted unsound fragment '{fragment}' for matching text '{text}'.");
            }
        }
    }

    private static IEnumerable<string> EnumerateTexts(string alphabet, int maxLength)
    {
        yield return string.Empty;
        for (var length = 1; length <= maxLength; length++)
        {
            foreach (var text in EnumerateTextsOfLength(alphabet, length))
            {
                yield return text;
            }
        }
    }

    private static IEnumerable<string> EnumerateTextsOfLength(string alphabet, int length)
    {
        if (length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        foreach (var prefix in EnumerateTextsOfLength(alphabet, length - 1))
        {
            for (var i = 0; i < alphabet.Length; i++)
            {
                yield return prefix + alphabet[i];
            }
        }
    }
}



