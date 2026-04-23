using System.Text;

namespace Lokad.Sift.LowLevel;

public static class Hashing
{
    public static ulong Fnv1A64(ReadOnlySpan<byte> data)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offset;
        foreach (var value in data)
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }

    public static bool ContainsUppercase(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) && Rune.ToUpperInvariant(rune) == rune && Rune.ToLowerInvariant(rune) != rune)
            {
                return true;
            }
        }

        return false;
    }
}
