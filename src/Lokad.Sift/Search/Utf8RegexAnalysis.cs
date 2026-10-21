using System.Text;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Lokad.Sift;
using Lokad.Sift.LowLevel;

namespace Lokad.Sift.Search;

internal static class Utf8RegexAnalysis
{
    public static bool TryParseSyntax(
        SearchQuery query,
        CaseMode resolvedCaseMode,
        [NotNullWhen(true)] out Utf8RegexSyntax? syntax)
    {
        syntax = null;
        var pattern = query.Pattern;
        if (query.PatternMode != PatternMode.Regex ||
            resolvedCaseMode == CaseMode.Smart ||
            !IsAscii(pattern))
        {
            return false;
        }

        var parser = new Parser(pattern, query.Multiline || query.WholeLine, query.DotAll, resolvedCaseMode == CaseMode.Insensitive);
        if (!parser.TryParse(out var root, out var multiline, out var dotAll, out var ignoreCase))
        {
            return false;
        }

        syntax = new Utf8RegexSyntax(root, multiline, dotAll, ignoreCase);
        return true;
    }

    public static bool TryCompile(
        SearchQuery query,
        bool isExactLiteral,
        Utf8RegexSyntax syntax,
        [NotNullWhen(true)] out Utf8RegexProgram? program)
    {
        program = null;
        if (isExactLiteral)
        {
            return false;
        }

        program = Compiler.Compile(
            RegexVerifier.BuildUtf8Pattern(query),
            syntax.Root,
            syntax.Multiline,
            syntax.DotAll,
            syntax.IgnoreCase);
        return true;
    }

    private static bool IsAscii(string text) => Encoding.UTF8.GetByteCount(text) == text.Length;

    private sealed class Parser
    {
        private readonly string _pattern;
        private readonly bool _multiline;
        private readonly bool _dotAll;
        private readonly bool _ignoreCase;
        private int _index;

        public Parser(string pattern, bool multiline, bool dotAll, bool ignoreCase)
        {
            _pattern = pattern;
            _multiline = multiline;
            _dotAll = dotAll;
            _ignoreCase = ignoreCase;
        }

        public bool TryParse(
            [NotNullWhen(true)] out Utf8RegexNode? root,
            out bool multiline,
            out bool dotAll,
            out bool ignoreCase)
        {
            root = null;
            if (!TryParseExpression(out var parsedRoot))
            {
                multiline = default;
                dotAll = default;
                ignoreCase = default;
                return false;
            }

            if (_index != _pattern.Length)
            {
                multiline = default;
                dotAll = default;
                ignoreCase = default;
                return false;
            }

            root = parsedRoot;
            multiline = _multiline;
            dotAll = _dotAll;
            ignoreCase = _ignoreCase;
            return true;
        }

        private bool TryParseExpression([NotNullWhen(true)] out Utf8RegexNode? node)
        {
            node = null;
            if (!TryParseSequence(out var first))
            {
                return false;
            }

            List<Utf8RegexNode>? options = null;
            while (Consume('|'))
            {
                options ??= [first];
                if (!TryParseSequence(out var next))
                {
                    return false;
                }

                options.Add(next);
            }

            node = options is null ? first : new Utf8RegexAlternation([.. options]);
            return true;
        }

        private bool TryParseSequence([NotNullWhen(true)] out Utf8RegexNode? node)
        {
            node = null;
            var children = new List<Utf8RegexNode>();
            while (_index < _pattern.Length &&
                   _pattern[_index] != ')' &&
                   _pattern[_index] != '|')
            {
                if (!TryParseQuantified(out var child))
                {
                    return false;
                }

                children.Add(child);
            }

            if (children.Count == 0)
            {
                return false;
            }

            node = children.Count == 1 ? children[0] : new Utf8RegexSequence([.. children]);
            return true;
        }

        private bool TryParseQuantified([NotNullWhen(true)] out Utf8RegexNode? node)
        {
            node = null;
            if (!TryParseAtom(out var atom))
            {
                return false;
            }

            if (!TryParseQuantifier(out var min, out var max))
            {
                return false;
            }

            node = min == 1 && max == 1 ? atom : new Utf8RegexRepeat(atom, min, max);
            return true;
        }

        private bool TryParseAtom([NotNullWhen(true)] out Utf8RegexNode? node)
        {
            node = null;
            if (_index >= _pattern.Length)
            {
                return false;
            }

            var current = _pattern[_index++];
            switch (current)
            {
                case '.':
                    node = new Utf8RegexAtom(Utf8RegexAtomKind.Dot, 0);
                    return true;
                case '[':
                    return TryParseCharClass(out node);
                case '\\':
                    if (TryParseBoundary(out var boundary))
                    {
                        node = boundary;
                        return true;
                    }

                    if (TryParseEscapeClass())
                    {
                        node = new Utf8RegexAtom(Utf8RegexAtomKind.CharClass, 0);
                        return true;
                    }

                    if (!TryParseEscape(out var literal))
                    {
                        return false;
                    }

                    node = new Utf8RegexAtom(Utf8RegexAtomKind.Literal, literal);
                    return true;
                case '(':
                    return TryParseGroup(out node);
                case '^':
                    node = new Utf8RegexAtom(Utf8RegexAtomKind.StartAnchor, 0);
                    return true;
                case '$':
                    node = new Utf8RegexAtom(Utf8RegexAtomKind.EndAnchor, 0);
                    return true;
                case ')':
                case '|':
                    return false;
                default:
                    if (current is '*' or '+' or '?' or '{' or '}')
                    {
                        return false;
                    }

                    node = new Utf8RegexAtom(Utf8RegexAtomKind.Literal, (byte)current);
                    return true;
            }
        }

        private bool TryParseGroup([NotNullWhen(true)] out Utf8RegexNode? node)
        {
            node = null;
            if (_index < _pattern.Length && _pattern[_index] == '?')
            {
                if (_index + 1 >= _pattern.Length || _pattern[_index + 1] != ':')
                {
                    return false;
                }

                _index += 2;
            }

            if (!TryParseExpression(out node))
            {
                return false;
            }

            return Consume(')');
        }

        private bool TryParseCharClass([NotNullWhen(true)] out Utf8RegexNode? node)
        {
            node = null;
            if (_index >= _pattern.Length)
            {
                return false;
            }

            if (_pattern[_index] == '^')
            {
                _index++;
            }

            var hasAny = false;
            while (_index < _pattern.Length && _pattern[_index] != ']')
            {
                if (!TryParseClassChar(out var start))
                {
                    return false;
                }

                if (_index + 1 < _pattern.Length && _pattern[_index] == '-' && _pattern[_index + 1] != ']')
                {
                    _index++;
                    if (!TryParseClassChar(out var end) || end < start)
                    {
                        return false;
                    }

                    hasAny = true;
                    continue;
                }

                hasAny = true;
            }

            if (_index >= _pattern.Length || _pattern[_index] != ']' || !hasAny)
            {
                return false;
            }

            _index++;
            node = new Utf8RegexAtom(Utf8RegexAtomKind.CharClass, 0);
            return true;
        }

        private bool TryParseClassChar(out byte value)
        {
            value = 0;
            if (_index >= _pattern.Length)
            {
                return false;
            }

            var current = _pattern[_index++];
            if (current == '\\')
            {
                return TryParseEscape(out value);
            }

            if (current > 0x7F)
            {
                return false;
            }

            value = (byte)current;
            return true;
        }

        private bool TryParseEscape(out byte value)
        {
            value = 0;
            if (_index >= _pattern.Length)
            {
                return false;
            }

            var escaped = _pattern[_index++];
            switch (escaped)
            {
                case 'x':
                    return TryReadHexEscape(2, out value);
                case 'u':
                    return TryReadUnicodeEscape(out value);
                case 'c':
                    return TryReadControlEscape(out value);
            }

            if (RegexEscapeDecoder.TryDecodeSimple(escaped, out var simple))
            {
                value = (byte)simple;
                return true;
            }

            value = escaped switch
            {
                '\\' => (byte)'\\',
                '.' => (byte)'.',
                '+' => (byte)'+',
                '*' => (byte)'*',
                '?' => (byte)'?',
                '[' => (byte)'[',
                ']' => (byte)']',
                '^' => (byte)'^',
                '$' => (byte)'$',
                '{' => (byte)'{',
                '}' => (byte)'}',
                '-' => (byte)'-',
                '(' => (byte)'(',
                ')' => (byte)')',
                '|' => (byte)'|',
                ':' => (byte)':',
                _ => 0
            };

            return value != 0;
        }

        private bool TryReadHexEscape(int digits, out byte value)
        {
            value = 0;
            if (_index + digits > _pattern.Length)
            {
                return false;
            }

            if (!RegexEscapeDecoder.TryDecodeHex(_pattern.AsSpan(_index, digits), out var parsed) ||
                (uint)parsed > 0x7F)
            {
                return false;
            }

            _index += digits;
            value = (byte)parsed;
            return true;
        }

        private bool TryReadUnicodeEscape(out byte value)
        {
            value = 0;
            if (_index + 4 > _pattern.Length)
            {
                return false;
            }

            if (!RegexEscapeDecoder.TryDecodeHex(_pattern.AsSpan(_index, 4), out var parsed) ||
                (uint)parsed > 0x7F)
            {
                return false;
            }

            _index += 4;
            value = (byte)parsed;
            return true;
        }

        private bool TryReadControlEscape(out byte value)
        {
            value = 0;
            if (_index >= _pattern.Length)
            {
                return false;
            }

            if (RegexEscapeDecoder.TryDecodeControl(_pattern[_index++], out var control))
            {
                value = (byte)control;
                return true;
            }

            return false;
        }

        private bool TryParseEscapeClass()
        {
            if (_index >= _pattern.Length || _pattern[_index] is not ('d' or 'D' or 's' or 'S' or 'w' or 'W'))
            {
                return false;
            }

            _index++;
            return true;
        }

        private bool TryParseBoundary([NotNullWhen(true)] out Utf8RegexNode? node)
        {
            node = null;
            if (_index >= _pattern.Length)
            {
                return false;
            }

            switch (_pattern[_index])
            {
                case 'b':
                    node = new Utf8RegexAtom(Utf8RegexAtomKind.WordBoundary, 0);
                    break;
                case 'B':
                    node = new Utf8RegexAtom(Utf8RegexAtomKind.NonWordBoundary, 0);
                    break;
                default:
                    return false;
            }

            _index++;
            return true;
        }

        private bool TryParseQuantifier(out int min, out int max)
        {
            min = 1;
            max = 1;
            if (_index >= _pattern.Length)
            {
                return true;
            }

            switch (_pattern[_index])
            {
                case '+':
                    _index++;
                    min = 1;
                    max = int.MaxValue;
                    ConsumeLazy();
                    return true;
                case '*':
                    _index++;
                    min = 0;
                    max = int.MaxValue;
                    ConsumeLazy();
                    return true;
                case '?':
                    _index++;
                    min = 0;
                    max = 1;
                    ConsumeLazy();
                    return true;
                case '{':
                    return TryParseRange(out min, out max);
                default:
                    return true;
            }
        }

        private bool TryParseRange(out int min, out int max)
        {
            min = 0;
            max = 0;
            _index++;
            if (!TryReadInt(out min))
            {
                return false;
            }

            max = min;
            if (_index < _pattern.Length && _pattern[_index] == ',')
            {
                _index++;
                if (_index < _pattern.Length && _pattern[_index] == '}')
                {
                    max = int.MaxValue;
                }
                else if (!TryReadInt(out max))
                {
                    return false;
                }
            }

            if (_index >= _pattern.Length || _pattern[_index] != '}' || max < min)
            {
                return false;
            }

            _index++;
            ConsumeLazy();
            return true;
        }

        private bool TryReadInt(out int value)
        {
            value = 0;
            var start = _index;
            while (_index < _pattern.Length && char.IsDigit(_pattern[_index]))
            {
                var digit = _pattern[_index++] - '0';
                if (value > (int.MaxValue - digit) / 10)
                {
                    while (_index < _pattern.Length && char.IsDigit(_pattern[_index]))
                    {
                        _index++;
                    }

                    return false;
                }

                value = value * 10 + digit;
            }

            return _index > start;
        }

        private void ConsumeLazy()
        {
            if (_index < _pattern.Length && _pattern[_index] == '?')
            {
                _index++;
            }
        }

        private bool Consume(char value)
        {
            if (_index < _pattern.Length && _pattern[_index] == value)
            {
                _index++;
                return true;
            }

            return false;
        }

    }

    private sealed class Compiler
    {
        private const int MaxLeadingLiteralSetOptions = 16;
        private const int MaxLeadingLiteralSetBytes = 64;

        public static Utf8RegexProgram Compile(string pattern, Utf8RegexNode root, bool multiline, bool dotAll, bool ignoreCase)
        {
            var leadingLiteralSet = TryBuildLeadingLiteralSet(root, ignoreCase);
            return new Utf8RegexProgram(
                pattern,
                multiline,
                dotAll,
                ignoreCase,
                leadingLiteralSet);
        }

        private static byte[][]? TryBuildLeadingLiteralSet(Utf8RegexNode root, bool ignoreCase)
        {
            if (!TryExpandLeadingLiteralSet(root, ignoreCase, out var set))
            {
                return null;
            }

            if (set.Count <= 1)
            {
                return null;
            }

            return [.. set];
        }

        private static bool TryExpandLeadingLiteralSet(Utf8RegexNode node, bool ignoreCase, out List<byte[]> set)
        {
            switch (node)
            {
                case Utf8RegexSequence sequence:
                    return TryExpandSequenceLeadingLiteralSet(sequence, ignoreCase, out set);
                case Utf8RegexAlternation alternation:
                    return TryExpandAlternationLeadingLiteralSet(alternation, ignoreCase, out set);
                case Utf8RegexAtom { Kind: Utf8RegexAtomKind.Literal } atom:
                    set =
                    [
                        [ignoreCase ? AsciiCase.Fold(atom.Literal) : atom.Literal]
                    ];
                    return true;
                case Utf8RegexAtom atom when IsZeroWidth(atom):
                    set = [[]];
                    return true;
                default:
                    set = [];
                    return false;
            }
        }

        private static bool TryExpandSequenceLeadingLiteralSet(Utf8RegexSequence sequence, bool ignoreCase, out List<byte[]> set)
        {
            set = [[]];
            var appendedAnyLiteral = false;

            for (var i = 0; i < sequence.Children.Length; i++)
            {
                if (IsZeroWidth(sequence.Children[i]))
                {
                    continue;
                }

                if (!TryExpandLeadingLiteralSet(sequence.Children[i], ignoreCase, out var childSet) || childSet.Count == 0)
                {
                    break;
                }

                if (!TryCombineLeadingLiteralSets(set, childSet, out set))
                {
                    return false;
                }

                appendedAnyLiteral = HasNonEmptyLiteral(set);
                if (!CanContinueLeadingLiteralSet(sequence.Children[i]))
                {
                    break;
                }
            }

            return appendedAnyLiteral;
        }

        private static bool TryExpandAlternationLeadingLiteralSet(Utf8RegexAlternation alternation, bool ignoreCase, out List<byte[]> set)
        {
            set = [];
            for (var i = 0; i < alternation.Options.Length; i++)
            {
                if (!TryExpandLeadingLiteralSet(alternation.Options[i], ignoreCase, out var optionSet) || optionSet.Count == 0)
                {
                    set = [];
                    return false;
                }

                var optionHasLiteral = false;
                for (var j = 0; j < optionSet.Count; j++)
                {
                    var literal = optionSet[j];
                    if (literal.Length == 0)
                    {
                        continue;
                    }

                    optionHasLiteral = true;
                    if (ContainsLiteral(set, literal))
                    {
                        continue;
                    }

                    if (set.Count >= MaxLeadingLiteralSetOptions || literal.Length > MaxLeadingLiteralSetBytes)
                    {
                        set = [];
                        return false;
                    }

                    set.Add(literal);
                }

                if (!optionHasLiteral)
                {
                    set = [];
                    return false;
                }
            }

            return set.Count > 0;
        }

        private static bool TryCombineLeadingLiteralSets(List<byte[]> prefixes, List<byte[]> suffixes, out List<byte[]> combined)
        {
            combined = new List<byte[]>(Math.Min(MaxLeadingLiteralSetOptions, prefixes.Count * Math.Max(1, suffixes.Count)));
            for (var i = 0; i < prefixes.Count; i++)
            {
                for (var j = 0; j < suffixes.Count; j++)
                {
                    var prefix = prefixes[i];
                    var suffix = suffixes[j];
                    if (prefix.Length + suffix.Length > MaxLeadingLiteralSetBytes)
                    {
                        return false;
                    }

                    var literal = new byte[prefix.Length + suffix.Length];
                    prefix.CopyTo(literal, 0);
                    suffix.CopyTo(literal, prefix.Length);
                    if (ContainsLiteral(combined, literal))
                    {
                        continue;
                    }

                    if (combined.Count >= MaxLeadingLiteralSetOptions)
                    {
                        return false;
                    }

                    combined.Add(literal);
                }
            }

            return combined.Count > 0;
        }

        private static bool HasNonEmptyLiteral(List<byte[]> set)
        {
            for (var i = 0; i < set.Count; i++)
            {
                if (set[i].Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CanContinueLeadingLiteralSet(Utf8RegexNode node)
        {
            return node switch
            {
                Utf8RegexSequence => true,
                Utf8RegexAlternation => true,
                Utf8RegexAtom { Kind: Utf8RegexAtomKind.Literal } => true,
                Utf8RegexAtom atom when IsZeroWidth(atom) => true,
                _ => false
            };
        }

        private static bool ContainsLiteral(List<byte[]> set, byte[] candidate)
        {
            for (var i = 0; i < set.Count; i++)
            {
                if (set[i].AsSpan().SequenceEqual(candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsZeroWidth(Utf8RegexNode node)
        {
            return node is Utf8RegexAtom atom &&
                   atom.Kind is Utf8RegexAtomKind.WordBoundary or
                       Utf8RegexAtomKind.NonWordBoundary or
                       Utf8RegexAtomKind.StartAnchor or
                       Utf8RegexAtomKind.EndAnchor;
        }

    }
}

internal sealed record Utf8RegexProgram(
    string Pattern,
    bool Multiline,
    bool DotAll,
    bool IgnoreCase,
    byte[][]? LeadingLiteralSet);

internal sealed record Utf8RegexSyntax(
    Utf8RegexNode Root,
    bool Multiline,
    bool DotAll,
    bool IgnoreCase);

internal abstract record Utf8RegexNode;

internal sealed record Utf8RegexSequence(Utf8RegexNode[] Children) : Utf8RegexNode;

internal sealed record Utf8RegexAlternation(Utf8RegexNode[] Options) : Utf8RegexNode;

internal sealed record Utf8RegexRepeat(Utf8RegexNode Child, int Min, int Max) : Utf8RegexNode;

internal sealed record Utf8RegexAtom(Utf8RegexAtomKind Kind, byte Literal) : Utf8RegexNode;

internal enum Utf8RegexAtomKind
{
    Literal,
    Dot,
    CharClass,
    WordBoundary,
    NonWordBoundary,
    StartAnchor,
    EndAnchor
}

internal sealed record PreparedUtf8Regex(
    ISiftCompiledRegex Regex,
    RegexTimeoutPolicy RegexTimeoutPolicy);
