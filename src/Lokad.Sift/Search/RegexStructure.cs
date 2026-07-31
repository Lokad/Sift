namespace Lokad.Sift.Search;

internal static class RegexStructure
{
    public static bool ExceedsNestingLimit(string pattern, int limit)
    {
        var depth = 0;
        var inCharacterClass = false;
        var escaped = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var current = pattern[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\')
            {
                escaped = true;
                continue;
            }

            if (inCharacterClass)
            {
                inCharacterClass = current != ']';
                continue;
            }

            if (current == '[')
            {
                inCharacterClass = true;
                continue;
            }

            if (current == '(')
            {
                depth++;
                if (depth > limit)
                {
                    return true;
                }
            }
            else if (current == ')' && depth > 0)
            {
                depth--;
            }
        }

        return false;
    }
}
