namespace Lokad.Sift.Search;

internal static class RegexStructure
{
    // Scan structural delimiters only. The regex implementation validates syntax.
    public static bool ExceedsNestingLimit(string pattern, int limit)
    {
        var depth = 0;
        var ignoreWhitespace = false;
        Stack<bool>? optionScopes = null;
        for (var i = 0; i < pattern.Length; i++)
        {
            var current = pattern[i];
            if (current == '\\')
            {
                SkipEscape(pattern, ref i);
                continue;
            }

            if (current == '[')
            {
                SkipCharacterClass(pattern, ref i);
                continue;
            }

            if (ignoreWhitespace && current == '#')
            {
                var end = pattern.IndexOf('\n', i);
                if (end < 0) break;
                i = end;
                continue;
            }

            if (current == '(')
            {
                if (pattern.AsSpan(i).StartsWith("(?#"))
                {
                    // Backslashes and brackets have no special meaning inside comments.
                    var end = pattern.IndexOf(')', i + 3);
                    if (end < 0) break;
                    i = end;
                    continue;
                }

                var nextIgnoreWhitespace = ignoreWhitespace;
                if (i + 1 < pattern.Length && pattern[i + 1] == '?' &&
                    TryReadOptions(pattern, i + 2, ref nextIgnoreWhitespace, out var optionsEnd))
                {
                    if (pattern[optionsEnd] == ')')
                    {
                        ignoreWhitespace = nextIgnoreWhitespace;
                        i = optionsEnd;
                        continue;
                    }

                    i = optionsEnd;
                }

                if (++depth > limit) return true;
                (optionScopes ??= new Stack<bool>()).Push(ignoreWhitespace);
                ignoreWhitespace = nextIgnoreWhitespace;
            }
            else if (current == ')' && depth > 0)
            {
                depth--;
                ignoreWhitespace = optionScopes!.Pop();
            }
        }

        return false;
    }

    private static bool TryReadOptions(string pattern, int start, ref bool ignoreWhitespace, out int end)
    {
        var enabled = true;
        var next = ignoreWhitespace;
        for (end = start; end < pattern.Length; end++)
        {
            switch (pattern[end])
            {
                case '-': enabled = false; break;
                case '+': enabled = true; break;
                case 'x' or 'X': next = enabled; break;
                case 'i' or 'I' or 'm' or 'M' or 'n' or 'N' or 's' or 'S': break;
                case ':' or ')':
                    ignoreWhitespace = next;
                    return true;
                default: return false;
            }
        }

        return false;
    }

    private static void SkipEscape(string pattern, ref int index)
    {
        // A control escape also consumes its argument, e.g. \\c[ is ESC, not '['.
        if (++index < pattern.Length && pattern[index] == 'c') index++;
    }

    private static void SkipCharacterClass(string pattern, ref int index)
    {
        var depth = 1;
        var hasCharacter = SkipClassPrefix(pattern, ref index);
        while (++index < pattern.Length)
        {
            if (pattern[index] == '\\') SkipEscape(pattern, ref index);
            else if (pattern[index] == ']')
            {
                if (--depth == 0) return;
            }
            else if (hasCharacter && pattern[index] == '-' &&
                index + 1 < pattern.Length && pattern[index + 1] == '[')
            {
                depth++;
                index++;
                hasCharacter = SkipClassPrefix(pattern, ref index);
                continue;
            }

            hasCharacter = true;
        }
    }

    private static bool SkipClassPrefix(string pattern, ref int index)
    {
        if (index + 1 < pattern.Length && pattern[index + 1] == '^') index++;
        // ']' is a literal when it is the first class character, including after '^'.
        if (index + 1 < pattern.Length && pattern[index + 1] == ']')
        {
            index++;
            return true;
        }

        return false;
    }
}
