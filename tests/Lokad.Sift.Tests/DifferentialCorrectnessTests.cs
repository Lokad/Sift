using System.Text.RegularExpressions;
using Lokad.Sift;

namespace Lokad.Sift.Tests;

public sealed class DifferentialCorrectnessTests
{
    public static TheoryData<SearchQuery, PathFilter> SearchCases => new()
    {
        { new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter() },
        { new SearchQuery("beta", PatternMode.Literal, CaseMode.Insensitive), new PathFilter() },
        { new SearchQuery("beta", PatternMode.Literal, CaseMode.Smart), new PathFilter() },
        { new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive) { WholeWord = true }, new PathFilter() },
        { new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive) { WholeLine = true }, new PathFilter() },
        { new SearchQuery("b[a-z]+a", PatternMode.Regex, CaseMode.Sensitive), new PathFilter() },
        { new SearchQuery("alpha\\nbeta", PatternMode.Regex, CaseMode.Sensitive) { Multiline = true }, new PathFilter() },
        { new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter { PathRegex = @"src/.+\.txt$" } },
        { new SearchQuery("beta", PatternMode.Literal, CaseMode.Sensitive), new PathFilter { IncludeGlobs = ["src/**/*.txt"], ExcludeGlobs = ["src/generated/**"] } }
    };

    [Theory]
    [MemberData(nameof(SearchCases))]
    public async Task SearchMatchesBruteForceBaseline(SearchQuery query, PathFilter filter)
    {
        using var fixture = new CorpusFixture(
            ("src/a.txt", "alpha beta\nbeta alpha\n"),
            ("src/generated/b.txt", "beta\n"),
            ("src/nested/c.txt", "alpha\nbeta\n"),
            ("tests/d.txt", "gamma Beta delta\n"),
            ("docs/e.md", "beta\n"),
            ("src/whole.txt", "beta\nalphabetabeta\n"));

        using var index = new SiftIndex(fixture.CorpusPath);
        await index.Build(fixture.CreateSource());

        using var snapshot = index.OpenSnapshot();
        var collector = new HitBuffer();
        snapshot.Search(query, filter, collector);

        var actual = collector.Hits
            .Select(hit => snapshot.Materialize(hit, 0, 0))
            .Select(static hit => (hit.Path, hit.StartLine, hit.StartColumn, hit.MatchText))
            .OrderBy(static hit => hit.Path, StringComparer.Ordinal)
            .ThenBy(static hit => hit.StartLine)
            .ThenBy(static hit => hit.StartColumn)
            .ToArray();

        var expected = fixture.Documents
            .Where(doc => MatchesPathFilter(doc.Path, filter))
            .SelectMany(doc => FindExpectedHits(doc.Path, doc.Content, query))
            .OrderBy(static hit => hit.Path, StringComparer.Ordinal)
            .ThenBy(static hit => hit.StartLine)
            .ThenBy(static hit => hit.StartColumn)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    private static IEnumerable<(string Path, int StartLine, int StartColumn, string MatchText)> FindExpectedHits(string path, string content, SearchQuery query)
    {
        return query.PatternMode == PatternMode.Literal
            ? FindLiteralHits(path, content, query)
            : FindRegexHits(path, content, query);
    }

    private static IEnumerable<(string Path, int StartLine, int StartColumn, string MatchText)> FindLiteralHits(string path, string content, SearchQuery query)
    {
        var comparison = ResolveComparison(query);
        var searchIndex = 0;
        while (searchIndex <= content.Length)
        {
            var found = content.IndexOf(query.Pattern, searchIndex, comparison);
            if (found < 0)
            {
                yield break;
            }

            var end = found + query.Pattern.Length;
            if (query.WholeWord && !IsWholeWord(content, found, end))
            {
                searchIndex = found + 1;
                continue;
            }

            if (query.WholeLine && !IsWholeLine(content, found, end))
            {
                searchIndex = found + 1;
                continue;
            }

            yield return (path, GetLine(content, found), GetColumn(content, found), content.Substring(found, query.Pattern.Length));
            searchIndex = found + 1;
        }
    }

    private static IEnumerable<(string Path, int StartLine, int StartColumn, string MatchText)> FindRegexHits(string path, string content, SearchQuery query)
    {
        var options = RegexOptions.CultureInvariant;
        if (query.Multiline)
        {
            options |= RegexOptions.Multiline;
        }

        if (query.DotAll)
        {
            options |= RegexOptions.Singleline;
        }

        if (ResolveComparison(query) == StringComparison.OrdinalIgnoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        var regex = new Regex(query.Pattern, options, TimeSpan.FromSeconds(1));
        foreach (Match match in regex.Matches(content))
        {
            yield return (path, GetLine(content, match.Index), GetColumn(content, match.Index), match.Value);
        }
    }

    private static StringComparison ResolveComparison(SearchQuery query)
    {
        return query.CaseMode switch
        {
            CaseMode.Sensitive => StringComparison.Ordinal,
            CaseMode.Insensitive => StringComparison.OrdinalIgnoreCase,
            CaseMode.Smart => query.Pattern.Any(char.IsUpper) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase,
            _ => StringComparison.Ordinal
        };
    }

    private static bool IsWholeWord(string text, int start, int end)
    {
        return IsBoundary(text, start - 1) && IsBoundary(text, end);
    }

    private static bool IsBoundary(string text, int index)
    {
        if (index < 0 || index >= text.Length)
        {
            return true;
        }

        return !char.IsLetterOrDigit(text[index]) && text[index] != '_';
    }

    private static bool IsWholeLine(string text, int start, int end)
    {
        var lineStart = start;
        while (lineStart > 0 && text[lineStart - 1] != '\n')
        {
            lineStart--;
        }

        var lineEnd = end;
        while (lineEnd < text.Length && text[lineEnd] != '\n')
        {
            lineEnd++;
        }

        return string.Equals(text[lineStart..lineEnd].TrimEnd('\r'), text[start..end], StringComparison.Ordinal);
    }

    private static int GetLine(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static int GetColumn(string text, int index)
    {
        var lineStart = 0;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                lineStart = i + 1;
            }
        }

        return index - lineStart + 1;
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
                if (Regex.IsMatch(path, GlobToRegex(filter.IncludeGlobs[i])))
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
                if (Regex.IsMatch(path, GlobToRegex(filter.ExcludeGlobs[i])))
                {
                    return false;
                }
            }
        }

        return string.IsNullOrWhiteSpace(filter.PathRegex) || Regex.IsMatch(path, filter.PathRegex);
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

            builder += current == '?' ? "[^/]" : Regex.Escape(current.ToString());
        }

        return builder + "$";
    }
}




