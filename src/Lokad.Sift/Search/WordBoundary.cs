using System.Buffers;
using System.Text;

namespace Lokad.Sift.Search;

internal static class WordBoundary
{
    public static bool IsWholeWord(ReadOnlySpan<char> text, int start, int end)
    {
        return IsBoundaryBefore(text, start) && IsBoundaryAfter(text, end);
    }

    private static bool IsBoundaryBefore(ReadOnlySpan<char> text, int index)
    {
        if (index <= 0)
        {
            return true;
        }

        // A regex may end on one UTF-16 code unit of a scalar; that is not a boundary.
        if (index < text.Length &&
            char.IsHighSurrogate(text[index - 1]) &&
            char.IsLowSurrogate(text[index]))
        {
            return false;
        }

        var status = Rune.DecodeLastFromUtf16(text[..index], out var rune, out _);
        return status != OperationStatus.Done || !IsWordRune(rune);
    }

    private static bool IsBoundaryAfter(ReadOnlySpan<char> text, int index)
    {
        if (index >= text.Length)
        {
            return true;
        }

        if (index > 0 &&
            char.IsHighSurrogate(text[index - 1]) &&
            char.IsLowSurrogate(text[index]))
        {
            return false;
        }

        var status = Rune.DecodeFromUtf16(text[index..], out var rune, out _);
        return status != OperationStatus.Done || !IsWordRune(rune);
    }

    private static bool IsWordRune(Rune rune)
    {
        return Rune.IsLetterOrDigit(rune) || rune.Value == '_';
    }
}
