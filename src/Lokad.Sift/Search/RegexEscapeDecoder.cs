namespace Lokad.Sift.Search;

internal static class RegexEscapeDecoder
{
    public static bool TryDecodeControl(char escaped, out char value)
    {
        var upper = escaped is >= 'a' and <= 'z'
            ? (char)(escaped - ('a' - 'A'))
            : escaped;
        if (upper is < 'A' or > 'Z')
        {
            value = default;
            return false;
        }

        value = (char)(upper - 'A' + 1);
        return true;
    }

    public static bool TryDecodeHex(ReadOnlySpan<char> digits, out int value)
    {
        value = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var hex = digits[i] switch
            {
                >= '0' and <= '9' => digits[i] - '0',
                >= 'a' and <= 'f' => digits[i] - 'a' + 10,
                >= 'A' and <= 'F' => digits[i] - 'A' + 10,
                _ => -1
            };
            if (hex < 0)
            {
                value = 0;
                return false;
            }

            value = (value << 4) | hex;
        }

        return true;
    }

    public static bool TryDecodeSimple(char escaped, out char value)
    {
        value = escaped switch
        {
            'a' => '\a',
            'e' => '\u001B',
            'f' => '\f',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            'v' => '\v',
            _ => default
        };
        return value != default;
    }
}
