using System.Buffers;
using System.Text;

namespace Lokad.Sift.LowLevel;

internal static class UnicodeText
{
    public static bool IsWellFormedUtf16(string text)
    {
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done)
                return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
}
