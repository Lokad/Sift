using System.Text;

namespace Lokad.Sift.Search;

internal static class RegexFragmentAnalyzer
{
    internal readonly record struct AdvisorySeedPlan(string[] Seeds, bool RequiresIgnoreCase);

    public static bool TryExtractExactLiteral(string pattern, out string literal)
    {
        literal = string.Empty;
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }

        var parser = new Parser(pattern, limit: 1);
        var result = parser.ParseExpression();
        if (!parser.IsComplete || result.ExactLiteral is null)
        {
            return false;
        }

        literal = result.ExactLiteral;
        return true;
    }

    public static string[] ExtractMandatoryFragments(string pattern, int limit = 8)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return [];
        }

        var parser = new Parser(pattern, limit);
        var result = parser.ParseExpression();
        if (!parser.IsComplete)
        {
            return [];
        }

        return SelectBestFragments(result.Fragments, limit);
    }

    public static string[] ExtractAdvisorySeeds(string pattern, int limit = 8)
    {
        return AnalyzeAdvisorySeeds(pattern, limit).Seeds;
    }

    public static AdvisorySeedPlan AnalyzeAdvisorySeeds(string pattern, int limit = 8)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return new AdvisorySeedPlan([], false);
        }

        var parser = new Parser(pattern, limit, advisoryMode: true);
        var result = parser.ParseExpression();
        if (!parser.IsComplete)
        {
            return new AdvisorySeedPlan([], parser.RequiresIgnoreCase);
        }

        var merged = new List<string>(result.AdvisorySeeds.Length + result.Fragments.Length);
        for (var i = 0; i < result.AdvisorySeeds.Length; i++)
        {
            merged.Add(result.AdvisorySeeds[i]);
        }

        for (var i = 0; i < result.Fragments.Length; i++)
        {
            if (!ContainsOrdinal(merged, result.Fragments[i]))
            {
                merged.Add(result.Fragments[i]);
            }
        }

        return new AdvisorySeedPlan(SelectBestFragments([.. merged], limit), parser.RequiresIgnoreCase);
    }

    private static string[] SelectBestFragments(string[] fragments, int limit)
    {
        if (fragments.Length == 0 || limit <= 0)
        {
            return [];
        }

        var buffer = new List<string>(fragments.Length);
        for (var i = 0; i < fragments.Length; i++)
        {
            var fragment = fragments[i];
            if (string.IsNullOrEmpty(fragment) || ContainsOrdinal(buffer, fragment))
            {
                continue;
            }

            buffer.Add(fragment);
        }

        if (buffer.Count <= 1)
        {
            return buffer.ToArray();
        }

        buffer.Sort(static (left, right) =>
        {
            var byLength = right.Length.CompareTo(left.Length);
            return byLength != 0 ? byLength : StringComparer.Ordinal.Compare(left, right);
        });

        var selected = new List<string>(Math.Min(limit, buffer.Count));
        for (var i = 0; i < buffer.Count; i++)
        {
            var fragment = buffer[i];
            if (IsRedundant(selected, fragment))
            {
                continue;
            }

            selected.Add(fragment);
            if (selected.Count == limit)
            {
                break;
            }
        }

        return selected.ToArray();
    }

    private static bool ContainsOrdinal(List<string> values, string candidate)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRedundant(List<string> selected, string candidate)
    {
        for (var i = 0; i < selected.Count; i++)
        {
            if (selected[i].Contains(candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Parser
    {
        private readonly string _pattern;
        private readonly int _limit;
        private readonly bool _advisoryMode;
        private int _index;
        private bool _unsupported;
        private bool _requiresIgnoreCase;

        public Parser(string pattern, int limit, bool advisoryMode = false)
        {
            _pattern = pattern;
            _limit = limit;
            _advisoryMode = advisoryMode;
        }

        public bool IsComplete => !_unsupported && _index == _pattern.Length;

        public bool RequiresIgnoreCase => _requiresIgnoreCase;

        public NodeResult ParseExpression()
        {
            var current = ParseSequence();
            if (_unsupported)
            {
                return NodeResult.None;
            }

            while (_index < _pattern.Length && _pattern[_index] == '|')
            {
                _index++;
                var right = ParseSequence();
                current = NodeResult.Intersect(current, right, _limit);
                if (_unsupported)
                {
                    return NodeResult.None;
                }
            }

            return current;
        }

        private NodeResult ParseSequence()
        {
            var result = NodeResult.SequenceSeed;
            while (_index < _pattern.Length)
            {
                var current = _pattern[_index];
                if (current is ')' or '|')
                {
                    break;
                }

                var atom = ParseAtom();
                atom = ApplyQuantifier(atom);
                result = NodeResult.Concat(result, atom, _limit);
                if (_unsupported)
                {
                    return NodeResult.None;
                }
            }

            return result;
        }

        private NodeResult ParseAtom()
        {
            if (_index >= _pattern.Length)
            {
                return NodeResult.None;
            }

            var current = _pattern[_index++];
            return current switch
            {
                '(' => ParseGroup(),
                '[' => ParseCharClass(),
                '\\' => ParseEscape(),
                '^' or '$' => NodeResult.ZeroWidth,
                '.' => NodeResult.Optional,
                _ => NodeResult.Literal(current.ToString())
            };
        }

        private NodeResult ParseGroup()
        {
            if (_index < _pattern.Length && _pattern[_index] == '?')
            {
                _index++;
                if (_index >= _pattern.Length)
                {
                    _unsupported = true;
                    return NodeResult.None;
                }

                var kind = _pattern[_index++];
                if (kind is ':' or '>')
                {
                    var inner = ParseExpression();
                    if (_index >= _pattern.Length || _pattern[_index] != ')')
                    {
                        _unsupported = true;
                        return NodeResult.None;
                    }

                    _index++;
                    return inner;
                }

                if (kind is '=' or '!')
                {
                    return ParseLookaroundGroup(isPositive: kind == '=');
                }

                if (kind == '#')
                {
                    while (_index < _pattern.Length && _pattern[_index] != ')')
                    {
                        _index++;
                    }

                    if (_index >= _pattern.Length || _pattern[_index] != ')')
                    {
                        _unsupported = true;
                        return NodeResult.None;
                    }

                    _index++;
                    return NodeResult.ZeroWidth;
                }

                if (_advisoryMode && TryParseScopedInlineOptions(kind, out var inlineIgnoreCase))
                {
                    var inner = ParseExpression();
                    if (_index >= _pattern.Length || _pattern[_index] != ')')
                    {
                        _unsupported = true;
                        return NodeResult.None;
                    }

                    _index++;
                    _requiresIgnoreCase |= inlineIgnoreCase;
                    return inner;
                }

                if (kind == '<' && _index < _pattern.Length && (_pattern[_index] == '=' || _pattern[_index] == '!'))
                {
                    var isPositive = _pattern[_index++] == '=';
                    return ParseLookaroundGroup(isPositive);
                }

                if (kind == '<' && TryConsumeNamedGroupHeader('>'))
                {
                    return ParseTransparentGroupBody();
                }

                if (kind == '\'' && TryConsumeNamedGroupHeader('\''))
                {
                    return ParseTransparentGroupBody();
                }

                _unsupported = true;
                return NodeResult.None;
            }

            var result = ParseExpression();
            if (_index >= _pattern.Length || _pattern[_index] != ')')
            {
                _unsupported = true;
                return NodeResult.None;
            }

            _index++;
            return result;
        }

        private NodeResult ParseLookaroundGroup(bool isPositive)
        {
            var inner = ParseExpression();
            if (_index >= _pattern.Length || _pattern[_index] != ')')
            {
                _unsupported = true;
                return NodeResult.None;
            }

            _index++;
            return isPositive
                ? NodeResult.Lookaround(inner.Fragments, inner.ExactLiteral)
                : NodeResult.ZeroWidth;
        }

        private NodeResult ParseTransparentGroupBody()
        {
            var inner = ParseExpression();
            if (_index >= _pattern.Length || _pattern[_index] != ')')
            {
                _unsupported = true;
                return NodeResult.None;
            }

            _index++;
            return inner;
        }

        private bool TryConsumeNamedGroupHeader(char delimiter)
        {
            var start = _index;
            while (_index < _pattern.Length && _pattern[_index] != delimiter)
            {
                _index++;
            }

            if (_index >= _pattern.Length || _index == start)
            {
                _unsupported = true;
                return false;
            }

            var name = _pattern[start.._index];
            if (name.IndexOf('-') >= 0 || name[0] is '=' or '!')
            {
                _unsupported = true;
                return false;
            }

            _index++;
            return true;
        }

        private NodeResult ParseCharClass()
        {
            if (_index >= _pattern.Length)
            {
                _unsupported = true;
                return NodeResult.None;
            }

            var negated = _pattern[_index] == '^';
            if (negated)
            {
                _index++;
            }

            var builder = new StringBuilder();
            var literalCount = 0;
            while (_index < _pattern.Length && _pattern[_index] != ']')
            {
                var current = _pattern[_index++];
                if (current == '\\')
                {
                    if (_index >= _pattern.Length)
                    {
                        _unsupported = true;
                        return NodeResult.None;
                    }

                    var escapedChar = _pattern[_index++];
                    if (TryParseSpecialEscape(escapedChar, out _))
                    {
                        literalCount = 2;
                        continue;
                    }

                    var escaped = ParseEscapedChar(escapedChar, out var literal);
                    if (!escaped)
                    {
                        _unsupported = true;
                        return NodeResult.None;
                    }

                    builder.Append(literal);
                    literalCount++;
                    continue;
                }

                if (current == '-')
                {
                    literalCount = 2;
                    continue;
                }

                if (literalCount > 0)
                {
                    literalCount = 2;
                }

                builder.Append(current);
                literalCount++;
            }

            if (_index >= _pattern.Length || _pattern[_index] != ']')
            {
                _unsupported = true;
                return NodeResult.None;
            }

            _index++;
            return !negated && literalCount == 1 ? NodeResult.Literal(builder.ToString()) : NodeResult.RequiredNonLiteral;
        }

        private NodeResult ParseEscape()
        {
            if (_index >= _pattern.Length)
            {
                _unsupported = true;
                return NodeResult.None;
            }

            var escaped = _pattern[_index++];
            if (escaped == 'c')
            {
                return ParseControlEscape();
            }

            if (escaped == 'x')
            {
                return ParseHexEscape(2);
            }

            if (escaped == 'u')
            {
                return ParseHexEscape(4);
            }

            if (escaped is >= '1' and <= '9')
            {
                while (_index < _pattern.Length && char.IsDigit(_pattern[_index]))
                {
                    _index++;
                }

                return NodeResult.RequiredNonLiteral;
            }

            if (escaped == 'k')
            {
                return ParseNamedBackreference();
            }

            if (TryParseSpecialEscape(escaped, out var special))
            {
                return special;
            }

            if (!ParseEscapedChar(escaped, out var literal))
            {
                _unsupported = true;
                return NodeResult.None;
            }

            return NodeResult.Literal(literal.ToString());
        }

        private bool TryParseScopedInlineOptions(char first, out bool ignoreCase)
        {
            ignoreCase = false;
            var cursor = _index - 1;
            var negate = false;
            var hasOption = false;

            while (cursor < _pattern.Length)
            {
                var current = cursor == _index - 1 ? first : _pattern[cursor];
                if (current == ':')
                {
                    _index = cursor + 1;
                    return hasOption;
                }

                if (current == '-')
                {
                    negate = true;
                    cursor++;
                    continue;
                }

                if (!char.IsLetter(current))
                {
                    return false;
                }

                hasOption = true;
                if (current == 'i')
                {
                    ignoreCase = !negate;
                }

                cursor++;
            }

            return false;
        }

        private NodeResult ParseNamedBackreference()
        {
            if (_index >= _pattern.Length)
            {
                _unsupported = true;
                return NodeResult.None;
            }

            var delimiter = _pattern[_index++];
            var terminator = delimiter switch
            {
                '<' => '>',
                '\'' => '\'',
                _ => '\0'
            };
            if (terminator == '\0')
            {
                _unsupported = true;
                return NodeResult.None;
            }

            var start = _index;
            while (_index < _pattern.Length && _pattern[_index] != terminator)
            {
                _index++;
            }

            if (_index >= _pattern.Length || _index == start)
            {
                _unsupported = true;
                return NodeResult.None;
            }

            _index++;
            return NodeResult.RequiredNonLiteral;
        }

        private NodeResult ParseControlEscape()
        {
            if (_index >= _pattern.Length)
            {
                _unsupported = true;
                return NodeResult.None;
            }

            var current = _pattern[_index++];
            if (current is >= 'a' and <= 'z')
            {
                current = (char)(current - 32);
            }

            if (current is < 'A' or > 'Z')
            {
                _unsupported = true;
                return NodeResult.None;
            }

            return NodeResult.Literal(((char)(current - 'A' + 1)).ToString());
        }

        private NodeResult ParseHexEscape(int digits)
        {
            if (_index > _pattern.Length - digits)
            {
                _unsupported = true;
                return NodeResult.None;
            }

            var value = 0;
            for (var i = 0; i < digits; i++)
            {
                var current = _pattern[_index++];
                var hex = current switch
                {
                    >= '0' and <= '9' => current - '0',
                    >= 'a' and <= 'f' => current - 'a' + 10,
                    >= 'A' and <= 'F' => current - 'A' + 10,
                    _ => -1
                };
                if (hex < 0)
                {
                    _unsupported = true;
                    return NodeResult.None;
                }

                value = (value << 4) | hex;
            }

            return NodeResult.Literal(char.ConvertFromUtf32(value));
        }

        private NodeResult ApplyQuantifier(NodeResult atom)
        {
            if (_index >= _pattern.Length)
            {
                return atom;
            }

            return _pattern[_index] switch
            {
                '*' or '?' => ConsumeOptional(),
                '+' => ConsumeRequired(atom),
                '{' => ConsumeRange(atom),
                _ => atom
            };

            NodeResult ConsumeOptional()
            {
                _index++;
                ConsumeLazy();
                return NodeResult.Optional;
            }

            NodeResult ConsumeRequired(NodeResult current)
            {
                _index++;
                ConsumeLazy();
                return current.MakeRepeated();
            }
        }

        private NodeResult ConsumeRange(NodeResult atom)
        {
            var start = _index;
            _index++;
            var min = 0;
            while (_index < _pattern.Length && char.IsDigit(_pattern[_index]))
            {
                min = checked(min * 10 + (_pattern[_index++] - '0'));
            }

            var max = min;
            if (_index < _pattern.Length && _pattern[_index] == ',')
            {
                _index++;
                max = 0;
                var hasMaximum = false;
                while (_index < _pattern.Length && char.IsDigit(_pattern[_index]))
                {
                    hasMaximum = true;
                    max = checked(max * 10 + (_pattern[_index++] - '0'));
                }

                max = hasMaximum ? max : int.MaxValue;
            }

            if (_index >= _pattern.Length || _pattern[_index] != '}')
            {
                _unsupported = true;
                return NodeResult.None;
            }

            _index++;
            ConsumeLazy();
            return min switch
            {
                0 => NodeResult.Optional,
                1 when max == 1 => atom,
                _ => atom.MakeRepeated()
            };
        }

        private void ConsumeLazy()
        {
            if (_index < _pattern.Length && _pattern[_index] == '?')
            {
                _index++;
            }
        }

        private static bool TryParseSpecialEscape(char escaped, out NodeResult result)
        {
            switch (escaped)
            {
                case 's':
                case 'S':
                case 'd':
                case 'D':
                case 'w':
                case 'W':
                    result = NodeResult.RequiredNonLiteral;
                    return true;
                case 'b':
                case 'B':
                case 'A':
                case 'G':
                case 'Z':
                case 'z':
                    result = NodeResult.ZeroWidth;
                    return true;
                default:
                    result = default;
                    return false;
            }
        }

        private static bool ParseEscapedChar(char escaped, out char literal)
        {
            switch (escaped)
            {
                case 'n':
                    literal = '\n';
                    return true;
                case 'r':
                    literal = '\r';
                    return true;
                case 't':
                    literal = '\t';
                    return true;
                case 'a':
                    literal = '\a';
                    return true;
                case 'f':
                    literal = '\f';
                    return true;
                case 'v':
                    literal = '\v';
                    return true;
                case 'e':
                    literal = '\u001B';
                    return true;
                case '\\':
                case '.':
                case '+':
                case '*':
                case '?':
                case '(':
                case ')':
                case '[':
                case ']':
                case '{':
                case '}':
                case '^':
                case '$':
                case '|':
                case '/':
                    literal = escaped;
                    return true;
                default:
                    literal = default;
                    return false;
            }
        }
    }

        private readonly record struct NodeResult(string[] Fragments, string[] AdvisorySeeds, bool CanBeEmpty, string RequiredPrefix, string RequiredSuffix, string? ExactLiteral)
    {
        public static NodeResult None => new([], [], true, string.Empty, string.Empty, null);
        public static NodeResult Optional => new([], [], true, string.Empty, string.Empty, null);
        public static NodeResult SequenceSeed => new([], [], false, string.Empty, string.Empty, string.Empty);
        public static NodeResult ZeroWidth => new([], [], false, string.Empty, string.Empty, null);
        public static NodeResult RequiredNonLiteral => new([], [], false, string.Empty, string.Empty, null);

        public static NodeResult Literal(string value) => new([value], [value], false, value, value, value);

        public static NodeResult Lookaround(string[] fragments, string? exactLiteral)
        {
            if (exactLiteral is not null && exactLiteral.Length > 0)
            {
                var values = new string[fragments.Length + 1];
                Array.Copy(fragments, values, fragments.Length);
                values[^1] = exactLiteral;
                var selected = SelectBestFragments(values, values.Length);
                return new(selected, selected, false, string.Empty, string.Empty, null);
            }

            return new(fragments, fragments, false, string.Empty, string.Empty, null);
        }

        public NodeResult MakeRepeated() => this with
        {
            CanBeEmpty = false,
            ExactLiteral = null
        };

        public static NodeResult Concat(NodeResult left, NodeResult right, int limit)
        {
            if (left.Fragments.Length == 0 && right.Fragments.Length == 0)
            {
                return new(
                    [],
                    BuildConcatAdvisorySeeds(left, right, limit),
                    left.CanBeEmpty && right.CanBeEmpty,
                    GetConcatPrefix(left, right),
                    GetConcatSuffix(left, right),
                    GetExactLiteral(left, right));
            }

            var fragments = new List<string>(left.Fragments.Length + right.Fragments.Length + 1);
            fragments.AddRange(left.Fragments);
            fragments.AddRange(right.Fragments);

            if (left.RequiredSuffix.Length > 0 && right.RequiredPrefix.Length > 0)
            {
                fragments.Add(left.RequiredSuffix + right.RequiredPrefix);
            }

            return new(
                SelectBestFragments([.. fragments], limit),
                BuildConcatAdvisorySeeds(left, right, limit),
                left.CanBeEmpty && right.CanBeEmpty,
                GetConcatPrefix(left, right),
                GetConcatSuffix(left, right),
                GetExactLiteral(left, right));
        }

        public static NodeResult Intersect(NodeResult left, NodeResult right, int limit)
        {
            if (left.Fragments.Length == 0 || right.Fragments.Length == 0)
            {
                var shared = BuildSharedAlternativeFragments(left, right, limit);
                return new(
                    shared,
                    BuildAlternationAdvisorySeeds(left, right, limit),
                    left.CanBeEmpty || right.CanBeEmpty,
                    CommonPrefix(left.RequiredPrefix, right.RequiredPrefix),
                    CommonSuffix(left.RequiredSuffix, right.RequiredSuffix),
                    GetAlternativeExactLiteral(left, right));
            }

            var intersection = BuildIntersection(left.Fragments, right.Fragments, limit);
            var sharedFragments = BuildSharedAlternativeFragments(left, right, limit);
            var merged = new List<string>(intersection.Length + sharedFragments.Length);
            for (var i = 0; i < intersection.Length; i++)
            {
                merged.Add(intersection[i]);
            }

            for (var i = 0; i < sharedFragments.Length; i++)
            {
                if (!ContainsOrdinal(merged, sharedFragments[i]))
                {
                    merged.Add(sharedFragments[i]);
                }
            }

            return new(
                SelectBestFragments([.. merged], limit),
                BuildAlternationAdvisorySeeds(left, right, limit),
                left.CanBeEmpty || right.CanBeEmpty,
                CommonPrefix(left.RequiredPrefix, right.RequiredPrefix),
                CommonSuffix(left.RequiredSuffix, right.RequiredSuffix),
                GetAlternativeExactLiteral(left, right));
        }

        private static string[] BuildConcatAdvisorySeeds(NodeResult left, NodeResult right, int limit)
        {
            if (limit <= 0)
            {
                return [];
            }

            var seeds = new List<string>(left.AdvisorySeeds.Length + right.AdvisorySeeds.Length + 1);
            if (!left.CanBeEmpty)
            {
                AddDistinct(seeds, left.AdvisorySeeds);
            }

            if (!right.CanBeEmpty)
            {
                AddDistinct(seeds, right.AdvisorySeeds);
            }

            var exactLiteral = GetExactLiteral(left, right);
            if (!string.IsNullOrEmpty(exactLiteral))
            {
                AddDistinct(seeds, [exactLiteral]);
            }

            return SelectBestFragments([.. seeds], limit);
        }

        private static string[] BuildAlternationAdvisorySeeds(NodeResult left, NodeResult right, int limit)
        {
            if (limit <= 0)
            {
                return [];
            }

            var leftSeeds = GetRequiredSeeds(left);
            var rightSeeds = GetRequiredSeeds(right);
            if (leftSeeds.Length == 0 || rightSeeds.Length == 0)
            {
                return [];
            }

            var seeds = new List<string>(leftSeeds.Length + rightSeeds.Length);
            AddDistinct(seeds, leftSeeds);
            AddDistinct(seeds, rightSeeds);
            return SelectBestFragments([.. seeds], limit);
        }

        private static string[] GetRequiredSeeds(NodeResult value)
        {
            if (value.CanBeEmpty)
            {
                return [];
            }

            if (!string.IsNullOrEmpty(value.ExactLiteral))
            {
                return [value.ExactLiteral];
            }

            return value.AdvisorySeeds;
        }

        private static void AddDistinct(List<string> target, string[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrEmpty(values[i]) && !ContainsOrdinal(target, values[i]))
                {
                    target.Add(values[i]);
                }
            }
        }

        private static string[] BuildSharedAlternativeFragments(NodeResult left, NodeResult right, int limit)
        {
            if (limit <= 0)
            {
                return [];
            }

            var fragments = new List<string>(2);
            var prefix = CommonPrefix(left.RequiredPrefix, right.RequiredPrefix);
            if (prefix.Length > 0)
            {
                fragments.Add(prefix);
            }

            var suffix = CommonSuffix(left.RequiredSuffix, right.RequiredSuffix);
            if (suffix.Length > 0)
            {
                var redundant = false;
                for (var i = 0; i < fragments.Count; i++)
                {
                    if (string.Equals(fragments[i], suffix, StringComparison.Ordinal))
                    {
                        redundant = true;
                        break;
                    }
                }

                if (!redundant)
                {
                    fragments.Add(suffix);
                }
            }

            return SelectBestFragments([.. fragments], limit);
        }

        private static string[] BuildIntersection(string[] left, string[] right, int limit)
        {
            if (left.Length == 0 || right.Length == 0 || limit <= 0)
            {
                return [];
            }

            var intersection = new List<string>(Math.Min(left.Length, right.Length));
            for (var i = 0; i < left.Length; i++)
            {
                var candidate = left[i];
                if (string.IsNullOrEmpty(candidate) || ContainsOrdinal(intersection, candidate))
                {
                    continue;
                }

                for (var j = 0; j < right.Length; j++)
                {
                    if (string.Equals(candidate, right[j], StringComparison.Ordinal))
                    {
                        intersection.Add(candidate);
                        break;
                    }
                }
            }

            return SelectBestFragments([.. intersection], limit);
        }

        private static string GetConcatPrefix(NodeResult left, NodeResult right)
        {
            var exactLiteral = GetExactLiteral(left, right);
            if (exactLiteral is not null)
            {
                return exactLiteral;
            }

            if (!left.CanBeEmpty)
            {
                if (left.ExactLiteral is not null && right.RequiredPrefix.Length > 0)
                {
                    return left.ExactLiteral + right.RequiredPrefix;
                }

                return left.RequiredPrefix;
            }

            return CommonPrefix(left.RequiredPrefix, right.RequiredPrefix);
        }

        private static string GetConcatSuffix(NodeResult left, NodeResult right)
        {
            var exactLiteral = GetExactLiteral(left, right);
            if (exactLiteral is not null)
            {
                return exactLiteral;
            }

            if (!right.CanBeEmpty)
            {
                if (right.ExactLiteral is not null && left.RequiredSuffix.Length > 0)
                {
                    return left.RequiredSuffix + right.ExactLiteral;
                }

                return right.RequiredSuffix;
            }

            return CommonSuffix(left.RequiredSuffix, right.RequiredSuffix);
        }

        private static string? GetExactLiteral(NodeResult left, NodeResult right)
        {
            return left.ExactLiteral is not null && right.ExactLiteral is not null
                ? left.ExactLiteral + right.ExactLiteral
                : null;
        }

        private static string? GetAlternativeExactLiteral(NodeResult left, NodeResult right)
        {
            return left.ExactLiteral is not null &&
                   right.ExactLiteral is not null &&
                   string.Equals(left.ExactLiteral, right.ExactLiteral, StringComparison.Ordinal)
                ? left.ExactLiteral
                : null;
        }

        private static string CommonPrefix(string left, string right)
        {
            var length = Math.Min(left.Length, right.Length);
            var index = 0;
            while (index < length && left[index] == right[index])
            {
                index++;
            }

            return index == 0 ? string.Empty : left[..index];
        }

        private static string CommonSuffix(string left, string right)
        {
            var length = Math.Min(left.Length, right.Length);
            var count = 0;
            while (count < length && left[left.Length - 1 - count] == right[right.Length - 1 - count])
            {
                count++;
            }

            return count == 0 ? string.Empty : left[^count..];
        }
    }
}
