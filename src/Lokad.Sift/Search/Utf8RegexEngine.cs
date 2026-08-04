using System.Text;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Lokad.Sift;
using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;
using CompiledUtf8Regex = Lokad.Utf8Regex.Utf8Regex;

namespace Lokad.Sift.Search;

internal static class Utf8RegexEngine
{
    internal const int CacheCapacity = 256;
    internal const int CachePatternCharacterCapacity = 256 * 1024;
    private static readonly BoundedLruCache<Utf8RegexCacheKey, CompiledUtf8Regex> Cache = new(
        CacheCapacity,
        CachePatternCharacterCapacity,
        static key => key.Pattern.Length);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);
    internal static int CacheCount => Cache.Count;
    internal static int CachePatternCharacters => Cache.Weight;

    public static bool TryCompile(
        in SearchQuery query,
        CaseMode resolvedCaseMode,
        [NotNullWhen(true)] out Utf8RegexProgram? program)
    {
        var analysis = RegexFragmentAnalyzer.AnalyzePattern(query.Pattern);
        return TryCompile(query, resolvedCaseMode, analysis.IsExactLiteral, out program);
    }

    public static bool TryCompile(
        in SearchQuery query,
        CaseMode resolvedCaseMode,
        bool isExactLiteral,
        [NotNullWhen(true)] out Utf8RegexProgram? program)
    {
        program = null;
        var pattern = RegexVerifier.BuildUtf8Pattern(query);
        if (query.PatternMode != PatternMode.Regex ||
            resolvedCaseMode == CaseMode.Smart ||
            !IsAscii(pattern) ||
            isExactLiteral)
        {
            return false;
        }

        var parser = new Parser(pattern, query.Multiline || query.WholeLine, query.DotAll, resolvedCaseMode == CaseMode.Insensitive);
        if (!parser.TryParse(out var root, out var multiline, out var dotAll, out var ignoreCase))
        {
            return false;
        }

        program = Compiler.Compile(pattern, root, multiline, dotAll, ignoreCase);
        return true;
    }

    public static bool TrySearchDocument(
        MappedSegment segment,
        SegmentDocument document,
        PreparedUtf8Regex prepared,
        PreparedOrderedAnchor[][]? preparedOrderedAnchorGroups,
        int hitLimit,
        IHitCollector collector,
        ref int hitsReturned,
        ref int byteRegexInvocations,
        ref int byteRegexMatchedDocuments,
        ref long byteRegexStartPositionsTried,
        ref SearchStopReason stopReason)
    {
        _ = preparedOrderedAnchorGroups;
        var contentBytes = segment.GetContentBytes(document);
        var encodedLineIndex = segment.GetEncodedLineIndexBytes(document);
        byteRegexInvocations++;

        var matched = false;
        try
        {
            // Stay on the byte-native API surface. If Sift ever needs to continue from a byte offset,
            // it should slice contentBytes or use a future byte-offset Utf8Regex API rather than a UTF-16 startAt overload.
            foreach (var match in prepared.Regex.EnumerateMatches(contentBytes))
            {
                if (hitsReturned >= hitLimit)
                {
                    break;
                }

                if (!match.TryGetByteRange(out var startByte, out var lengthInBytes))
                {
                    continue;
                }

                matched = true;
                byteRegexStartPositionsTried++;
                var endByte = startByte + lengthInBytes;
                var startLine = LineMap.GetLineFromByteOffset(encodedLineIndex, startByte);
                var endLine = LineMap.GetLineFromByteOffset(encodedLineIndex, Math.Max(startByte, endByte == 0 ? startByte : endByte - 1));
                var shouldContinue = collector.OnHit(
                    new RawHit(segment.SegmentId, document.Ordinal, startByte, endByte, startLine, endLine));
                hitsReturned++;
                if (!shouldContinue)
                {
                    stopReason = SearchStopReason.Cancelled;
                    return true;
                }

                if (hitsReturned >= hitLimit)
                {
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            if (prepared.TimeoutPolicy == TimeoutPolicy.Throw)
            {
                throw;
            }

            stopReason = SearchStopReason.RegexTimeout;
            return false;
        }

        if (matched)
        {
            byteRegexMatchedDocuments++;
        }

        return matched;
    }

    public static PreparedUtf8Regex Prepare(Utf8RegexProgram program, TimeoutPolicy timeoutPolicy)
    {
        var options = RegexOptions.CultureInvariant;
        if (program.Multiline)
        {
            options |= RegexOptions.Multiline;
        }

        if (program.DotAll)
        {
            options |= RegexOptions.Singleline;
        }

        if (program.IgnoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        var key = new Utf8RegexCacheKey(program.Pattern, options);
        var regex = Cache.GetOrAdd(
            key,
            static cacheKey => new CompiledUtf8Regex(cacheKey.Pattern, cacheKey.Options, DefaultTimeout),
            out var cacheHit);
        return new PreparedUtf8Regex(regex, cacheHit, timeoutPolicy);
    }

    private static bool IsAscii(string text) => Encoding.UTF8.GetByteCount(text) == text.Length;

    private static byte FoldAscii(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
    }

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

            if (!TryParseQuantifier(out var min, out var max, out var lazy))
            {
                return false;
            }

            node = min == 1 && max == 1 ? atom : new Utf8RegexRepeat(atom, min, max, lazy);
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
                    node = new Utf8RegexAtom(Utf8RegexAtomKind.Dot, 0, null, false);
                    return true;
                case '[':
                    return TryParseCharClass(out node);
                case '\\':
                    if (TryParseBoundary(out var boundary))
                    {
                        node = boundary;
                        return true;
                    }

                    if (TryParseEscapeClass(out var classSet, out var negated))
                    {
                        node = new Utf8RegexAtom(Utf8RegexAtomKind.CharClass, 0, classSet, negated);
                        return true;
                    }

                    if (!TryParseEscape(out var literal))
                    {
                        return false;
                    }

                    node = new Utf8RegexAtom(Utf8RegexAtomKind.Literal, NormalizeLiteral(literal), null, false);
                    return true;
                case '(':
                    return TryParseGroup(out node);
                case '^':
                    node = new Utf8RegexAtom(Utf8RegexAtomKind.StartAnchor, 0, null, false);
                    return true;
                case '$':
                    node = new Utf8RegexAtom(Utf8RegexAtomKind.EndAnchor, 0, null, false);
                    return true;
                case ')':
                case '|':
                    return false;
                default:
                    if (current is '*' or '+' or '?' or '{' or '}')
                    {
                        return false;
                    }

                    node = new Utf8RegexAtom(Utf8RegexAtomKind.Literal, NormalizeLiteral((byte)current), null, false);
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

            var negated = false;
            if (_pattern[_index] == '^')
            {
                negated = true;
                _index++;
            }

            var set = new bool[128];
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

                    for (var current = start; current <= end; current++)
                    {
                        set[current] = true;
                    }

                    hasAny = true;
                    continue;
                }

                set[start] = true;
                hasAny = true;
            }

            if (_index >= _pattern.Length || _pattern[_index] != ']' || !hasAny)
            {
                return false;
            }

            _index++;
            if (_ignoreCase)
            {
                AddAsciiCasePairs(set);
            }

            node = new Utf8RegexAtom(Utf8RegexAtomKind.CharClass, 0, set, negated);
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

            value = escaped switch
            {
                'a' => (byte)'\a',
                'e' => 0x1B,
                'f' => (byte)'\f',
                'n' => (byte)'\n',
                'r' => (byte)'\r',
                't' => (byte)'\t',
                'v' => (byte)'\v',
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

            var parsed = 0;
            for (var i = 0; i < digits; i++)
            {
                var hex = ParseHexDigit(_pattern[_index + i]);
                if (hex < 0)
                {
                    return false;
                }

                parsed = (parsed << 4) | hex;
            }

            _index += digits;
            if ((uint)parsed > 0x7F)
            {
                return false;
            }

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

            var parsed = 0;
            for (var i = 0; i < 4; i++)
            {
                var hex = ParseHexDigit(_pattern[_index + i]);
                if (hex < 0)
                {
                    return false;
                }

                parsed = (parsed << 4) | hex;
            }

            _index += 4;
            if ((uint)parsed > 0x7F)
            {
                return false;
            }

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

            var current = _pattern[_index++];
            if (current is >= 'A' and <= 'Z')
            {
                value = (byte)(current - 'A' + 1);
                return true;
            }

            if (current is >= 'a' and <= 'z')
            {
                value = (byte)(current - 'a' + 1);
                return true;
            }

            return false;
        }

        private static int ParseHexDigit(char value)
        {
            return value switch
            {
                >= '0' and <= '9' => value - '0',
                >= 'a' and <= 'f' => value - 'a' + 10,
                >= 'A' and <= 'F' => value - 'A' + 10,
                _ => -1
            };
        }

        private bool TryParseEscapeClass(out bool[]? set, out bool negated)
        {
            set = null;
            negated = false;
            if (_index >= _pattern.Length)
            {
                return false;
            }

            set = _pattern[_index] switch
            {
                'd' => BuildAsciiSet(static value => value is >= (byte)'0' and <= (byte)'9'),
                'D' => BuildAsciiSet(static value => value is >= (byte)'0' and <= (byte)'9'),
                's' => BuildAsciiSet(static value => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\v'),
                'S' => BuildAsciiSet(static value => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\v'),
                'w' => BuildAsciiSet(static value => value is >= (byte)'A' and <= (byte)'Z' ||
                                                  value is >= (byte)'a' and <= (byte)'z' ||
                                                  value is >= (byte)'0' and <= (byte)'9' ||
                                                  value == (byte)'_'),
                'W' => BuildAsciiSet(static value => value is >= (byte)'A' and <= (byte)'Z' ||
                                                  value is >= (byte)'a' and <= (byte)'z' ||
                                                  value is >= (byte)'0' and <= (byte)'9' ||
                                                  value == (byte)'_'),
                _ => null
            };
            if (set is null)
            {
                return false;
            }

            negated = _pattern[_index] is 'D' or 'S' or 'W';
            _index++;
            if (_ignoreCase && !negated)
            {
                AddAsciiCasePairs(set);
            }

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
                    node = new Utf8RegexAtom(Utf8RegexAtomKind.WordBoundary, 0, null, false);
                    break;
                case 'B':
                    node = new Utf8RegexAtom(Utf8RegexAtomKind.NonWordBoundary, 0, null, false);
                    break;
                default:
                    return false;
            }

            _index++;
            return true;
        }

        private bool TryParseQuantifier(out int min, out int max, out bool lazy)
        {
            min = 1;
            max = 1;
            lazy = false;
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
                    lazy = ConsumeLazy();
                    return true;
                case '*':
                    _index++;
                    min = 0;
                    max = int.MaxValue;
                    lazy = ConsumeLazy();
                    return true;
                case '?':
                    _index++;
                    min = 0;
                    max = 1;
                    lazy = ConsumeLazy();
                    return true;
                case '{':
                    return TryParseRange(out min, out max, out lazy);
                default:
                    return true;
            }
        }

        private bool TryParseRange(out int min, out int max, out bool lazy)
        {
            min = 0;
            max = 0;
            lazy = false;
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
            lazy = ConsumeLazy();
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

        private bool ConsumeLazy()
        {
            if (_index < _pattern.Length && _pattern[_index] == '?')
            {
                _index++;
                return true;
            }

            return false;
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

        private byte NormalizeLiteral(byte value)
        {
            return _ignoreCase ? FoldAscii(value) : value;
        }

        private static void AddAsciiCasePairs(bool[] set)
        {
            for (var value = (byte)'A'; value <= (byte)'Z'; value++)
            {
                if (set[value] || set[value + 32])
                {
                    set[value] = true;
                    set[value + 32] = true;
                }
            }
        }

        private static bool[] BuildAsciiSet(Func<byte, bool> predicate)
        {
            var set = new bool[128];
            for (var value = 0; value < set.Length; value++)
            {
                set[value] = predicate((byte)value);
            }

            return set;
        }
    }

    private sealed class Compiler
    {
        private const int MaxLeadingLiteralSetOptions = 16;
        private const int MaxLeadingLiteralSetBytes = 64;
        private const int MaxOrderedAnchorGroups = 16;
        private const int MaxOrderedAnchorsPerGroup = 4;
        private const int MaxOrderedAnchorBytes = 32;

        public static Utf8RegexProgram Compile(string pattern, Utf8RegexNode root, bool multiline, bool dotAll, bool ignoreCase)
        {
            var leadingLiteralSet = TryBuildLeadingLiteralSet(root, ignoreCase);
            var orderedAnchorGroups = TryBuildOrderedAnchorGroups(root, ignoreCase);
            return new Utf8RegexProgram(
                pattern,
                multiline,
                dotAll,
                ignoreCase,
                leadingLiteralSet,
                orderedAnchorGroups);
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
                        [ignoreCase ? FoldAscii(atom.Literal) : atom.Literal]
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

        private static Utf8RegexOrderedAnchorGroup[]? TryBuildOrderedAnchorGroups(Utf8RegexNode root, bool ignoreCase)
        {
            if (!TryCollectOrderedAnchorGroups(root, ignoreCase, out var groups))
            {
                return null;
            }

            var filtered = new List<Utf8RegexOrderedAnchorGroup>(groups.Count);
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (group.Count == 0)
                {
                    continue;
                }

                filtered.Add(new Utf8RegexOrderedAnchorGroup([.. group]));
            }

            return filtered.Count == 0 ? null : [.. filtered];
        }

        private static bool TryCollectOrderedAnchorGroups(Utf8RegexNode node, bool ignoreCase, out List<List<Utf8RegexOrderedAnchor>> groups)
        {
            switch (node)
            {
                case Utf8RegexSequence sequence:
                    return TryCollectSequenceOrderedAnchorGroups(sequence, ignoreCase, out groups);
                case Utf8RegexAlternation alternation:
                    return TryCollectAlternationOrderedAnchorGroups(alternation, ignoreCase, out groups);
                case Utf8RegexAtom { Kind: Utf8RegexAtomKind.Literal } atom:
                    groups =
                    [
                        [
                            new Utf8RegexOrderedAnchor([ignoreCase ? FoldAscii(atom.Literal) : atom.Literal], null, false)
                        ]
                    ];
                    return true;
                case Utf8RegexAtom atom when IsZeroWidth(atom):
                    groups =
                    [
                        []
                    ];
                    return true;
                default:
                    groups = [];
                    return false;
            }
        }

        private static bool TryCollectSequenceOrderedAnchorGroups(Utf8RegexSequence sequence, bool ignoreCase, out List<List<Utf8RegexOrderedAnchor>> groups)
        {
            groups =
            [
                []
            ];
            var appendToExisting = true;
            var anchorCount = 0;
            OrderedAnchorSeparator? pendingSeparator = null;

            for (var i = 0; i < sequence.Children.Length; i++)
            {
                var child = sequence.Children[i];
                if (IsZeroWidth(child))
                {
                    continue;
                }

                if (!TryCollectOrderedAnchorGroups(child, ignoreCase, out var childGroups))
                {
                    if (TryAnalyzeOrderedAnchorSeparator(child, out var separator))
                    {
                        pendingSeparator = pendingSeparator is null
                            ? separator
                            : CombineSeparators(pendingSeparator.Value, separator);
                    }
                    else
                    {
                        pendingSeparator = null;
                    }
                    appendToExisting = false;
                    continue;
                }

                if (!TryCombineOrderedAnchorGroups(groups, childGroups, appendToExisting, pendingSeparator, out groups))
                {
                    groups = [];
                    return false;
                }

                appendToExisting = true;
                pendingSeparator = null;
                anchorCount = Math.Max(anchorCount, GetMaxAnchorCount(groups));
                if (anchorCount > MaxOrderedAnchorsPerGroup)
                {
                    groups = [];
                    return false;
                }
            }

            return HasAnyOrderedAnchor(groups);
        }

        private static bool TryCollectAlternationOrderedAnchorGroups(Utf8RegexAlternation alternation, bool ignoreCase, out List<List<Utf8RegexOrderedAnchor>> groups)
        {
            groups = [];
            for (var i = 0; i < alternation.Options.Length; i++)
            {
                if (!TryCollectOrderedAnchorGroups(alternation.Options[i], ignoreCase, out var optionGroups))
                {
                    groups = [];
                    return false;
                }

                var optionHasAnchor = false;
                for (var j = 0; j < optionGroups.Count; j++)
                {
                    if (optionGroups[j].Count == 0)
                    {
                        continue;
                    }

                    optionHasAnchor = true;
                    if (groups.Count >= MaxOrderedAnchorGroups)
                    {
                        groups = [];
                        return false;
                    }

                    groups.Add(CloneAnchorGroup(optionGroups[j]));
                }

                if (!optionHasAnchor)
                {
                    groups = [];
                    return false;
                }
            }

            return groups.Count > 0;
        }

        private static bool TryCombineOrderedAnchorGroups(List<List<Utf8RegexOrderedAnchor>> prefixes, List<List<Utf8RegexOrderedAnchor>> suffixes, bool appendToExisting, OrderedAnchorSeparator? pendingSeparator, out List<List<Utf8RegexOrderedAnchor>> combined)
        {
            combined = new List<List<Utf8RegexOrderedAnchor>>(Math.Min(MaxOrderedAnchorGroups, prefixes.Count * Math.Max(1, suffixes.Count)));
            for (var i = 0; i < prefixes.Count; i++)
            {
                for (var j = 0; j < suffixes.Count; j++)
                {
                    if (combined.Count >= MaxOrderedAnchorGroups)
                    {
                        combined = [];
                        return false;
                    }

                    var merged = CloneAnchorGroup(prefixes[i]);
                    var suffix = suffixes[j];
                    if (suffix.Count > 0)
                    {
                        for (var anchorIndex = 0; anchorIndex < suffix.Count; anchorIndex++)
                        {
                            var anchor = suffix[anchorIndex];
                            if (anchor.Utf8Bytes.Length == 0)
                            {
                                continue;
                            }

                            if (appendToExisting && anchorIndex == 0 && merged.Count > 0)
                            {
                                var last = merged[^1];
                                if (last.Utf8Bytes.Length + anchor.Utf8Bytes.Length > MaxOrderedAnchorBytes)
                                {
                                    combined = [];
                                    return false;
                                }

                                var appended = new byte[last.Utf8Bytes.Length + anchor.Utf8Bytes.Length];
                                last.Utf8Bytes.CopyTo(appended, 0);
                                anchor.Utf8Bytes.CopyTo(appended, last.Utf8Bytes.Length);
                                merged[^1] = new Utf8RegexOrderedAnchor(appended, last.MaxGapBytesFromPrevious, last.SameLineFromPrevious);
                            }
                            else
                            {
                                if (anchor.Utf8Bytes.Length > MaxOrderedAnchorBytes)
                                {
                                    combined = [];
                                    return false;
                                }

                                var maxGapFromPrevious = anchor.MaxGapBytesFromPrevious;
                                var sameLineFromPrevious = anchor.SameLineFromPrevious;
                                if (anchorIndex == 0 && merged.Count > 0 && pendingSeparator is not null)
                                {
                                    maxGapFromPrevious = pendingSeparator.Value.MaxGapBytes;
                                    sameLineFromPrevious = pendingSeparator.Value.SameLineOnly;
                                }

                                merged.Add(new Utf8RegexOrderedAnchor([.. anchor.Utf8Bytes], maxGapFromPrevious, sameLineFromPrevious));
                            }
                        }
                    }

                    if (merged.Count > MaxOrderedAnchorsPerGroup)
                    {
                        combined = [];
                        return false;
                    }

                    combined.Add(merged);
                }
            }

            return combined.Count > 0;
        }

        private static List<Utf8RegexOrderedAnchor> CloneAnchorGroup(List<Utf8RegexOrderedAnchor> source)
        {
            var clone = new List<Utf8RegexOrderedAnchor>(source.Count);
            for (var i = 0; i < source.Count; i++)
            {
                clone.Add(new Utf8RegexOrderedAnchor([.. source[i].Utf8Bytes], source[i].MaxGapBytesFromPrevious, source[i].SameLineFromPrevious));
            }

            return clone;
        }

        private static bool HasAnyOrderedAnchor(List<List<Utf8RegexOrderedAnchor>> groups)
        {
            for (var i = 0; i < groups.Count; i++)
            {
                if (groups[i].Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static int GetMaxAnchorCount(List<List<Utf8RegexOrderedAnchor>> groups)
        {
            var max = 0;
            for (var i = 0; i < groups.Count; i++)
            {
                if (groups[i].Count > max)
                {
                    max = groups[i].Count;
                }
            }

            return max;
        }

        private static bool TryAnalyzeOrderedAnchorSeparator(Utf8RegexNode node, out OrderedAnchorSeparator separator)
        {
            switch (node)
            {
                case Utf8RegexAtom atom:
                    return TryAnalyzeOrderedAnchorSeparatorAtom(atom, out separator);
                case Utf8RegexRepeat repeat:
                    return TryAnalyzeOrderedAnchorSeparatorRepeat(repeat, out separator);
                case Utf8RegexSequence sequence:
                    return TryAnalyzeOrderedAnchorSeparatorSequence(sequence, out separator);
                case Utf8RegexAlternation alternation:
                    return TryAnalyzeOrderedAnchorSeparatorAlternation(alternation, out separator);
                default:
                    separator = default;
                    return false;
            }
        }

        private static bool TryAnalyzeOrderedAnchorSeparatorAtom(Utf8RegexAtom atom, out OrderedAnchorSeparator separator)
        {
            switch (atom.Kind)
            {
                case Utf8RegexAtomKind.CharClass:
                    {
                        if (atom.AsciiSet is not { } asciiSet)
                        {
                            separator = default;
                            return false;
                        }

                        var excludesNewline = atom.Negated
                            ? asciiSet[(byte)'\r'] && asciiSet[(byte)'\n']
                            : !asciiSet[(byte)'\r'] && !asciiSet[(byte)'\n'];
                        separator = new OrderedAnchorSeparator(1, excludesNewline);
                        return true;
                    }
                case Utf8RegexAtomKind.Dot:
                    separator = new OrderedAnchorSeparator(1, false);
                    return true;
                default:
                    separator = default;
                    return false;
            }
        }

        private static bool TryAnalyzeOrderedAnchorSeparatorRepeat(Utf8RegexRepeat repeat, out OrderedAnchorSeparator separator)
        {
            if (!TryAnalyzeOrderedAnchorSeparator(repeat.Child, out var child))
            {
                separator = default;
                return false;
            }

            int? maxGap = child.MaxGapBytes;
            if (repeat.Max == int.MaxValue)
            {
                maxGap = null;
            }
            else if (maxGap is { } boundedGap)
            {
                maxGap = boundedGap == 0 || repeat.Max <= int.MaxValue / boundedGap
                    ? boundedGap * repeat.Max
                    : null;
            }

            separator = new OrderedAnchorSeparator(maxGap, child.SameLineOnly);
            return true;
        }

        private static bool TryAnalyzeOrderedAnchorSeparatorSequence(Utf8RegexSequence sequence, out OrderedAnchorSeparator separator)
        {
            OrderedAnchorSeparator? combined = null;
            for (var i = 0; i < sequence.Children.Length; i++)
            {
                var child = sequence.Children[i];
                if (IsZeroWidth(child))
                {
                    continue;
                }

                if (!TryAnalyzeOrderedAnchorSeparator(child, out var childSeparator))
                {
                    separator = default;
                    return false;
                }

                combined = combined is null ? childSeparator : CombineSeparators(combined.Value, childSeparator);
            }

            if (combined is null)
            {
                separator = default;
                return false;
            }

            separator = combined.Value;
            return true;
        }

        private static bool TryAnalyzeOrderedAnchorSeparatorAlternation(Utf8RegexAlternation alternation, out OrderedAnchorSeparator separator)
        {
            OrderedAnchorSeparator? combined = null;
            for (var i = 0; i < alternation.Options.Length; i++)
            {
                if (!TryAnalyzeOrderedAnchorSeparator(alternation.Options[i], out var option))
                {
                    separator = default;
                    return false;
                }

                combined = combined is null
                    ? option
                    : new OrderedAnchorSeparator(
                        combined.Value.MaxGapBytes is null || option.MaxGapBytes is null
                            ? null
                            : Math.Max(combined.Value.MaxGapBytes.Value, option.MaxGapBytes.Value),
                        combined.Value.SameLineOnly && option.SameLineOnly);
            }

            if (combined is null)
            {
                separator = default;
                return false;
            }

            separator = combined.Value;
            return true;
        }

        private static OrderedAnchorSeparator CombineSeparators(OrderedAnchorSeparator left, OrderedAnchorSeparator right)
        {
            return new OrderedAnchorSeparator(
                left.MaxGapBytes is null || right.MaxGapBytes is null
                    ? null
                    : left.MaxGapBytes.Value <= int.MaxValue - right.MaxGapBytes.Value
                        ? left.MaxGapBytes.Value + right.MaxGapBytes.Value
                        : null,
                left.SameLineOnly && right.SameLineOnly);
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
    byte[][]? LeadingLiteralSet,
    Utf8RegexOrderedAnchorGroup[]? OrderedAnchorGroups);

internal readonly record struct Utf8RegexCacheKey(
    string Pattern,
    RegexOptions Options);

internal readonly record struct Utf8RegexOrderedAnchorGroup(
    Utf8RegexOrderedAnchor[] Anchors);

internal readonly record struct Utf8RegexOrderedAnchor(
    byte[] Utf8Bytes,
    int? MaxGapBytesFromPrevious,
    bool SameLineFromPrevious);

internal readonly record struct OrderedAnchorSeparator(
    int? MaxGapBytes,
    bool SameLineOnly);

internal abstract record Utf8RegexNode;

internal sealed record Utf8RegexSequence(Utf8RegexNode[] Children) : Utf8RegexNode;

internal sealed record Utf8RegexAlternation(Utf8RegexNode[] Options) : Utf8RegexNode;

internal sealed record Utf8RegexRepeat(Utf8RegexNode Child, int Min, int Max, bool Lazy) : Utf8RegexNode;

internal sealed record Utf8RegexAtom(Utf8RegexAtomKind Kind, byte Literal, bool[]? AsciiSet, bool Negated) : Utf8RegexNode;

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
    CompiledUtf8Regex Regex,
    bool CacheHit,
    TimeoutPolicy TimeoutPolicy);
