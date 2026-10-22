using System.Text;

namespace Lokad.Sift.Search;

internal struct Utf8OffsetCursor
{
    private int _charIndex;
    private int _byteOffset;

    public int Map(ReadOnlySpan<char> text, int targetCharIndex)
    {
        if (targetCharIndex < _charIndex)
        {
            throw new InvalidOperationException("Character offsets must be mapped in source order.");
        }

        if (targetCharIndex > 0 && targetCharIndex < text.Length &&
            char.IsHighSurrogate(text[targetCharIndex - 1]) && char.IsLowSurrogate(text[targetCharIndex]))
        {
            // Both UTF-16 halves project onto the scalar's UTF-8 start.
            targetCharIndex--;
        }

        if (targetCharIndex != _charIndex)
        {
            _byteOffset += Encoding.UTF8.GetByteCount(text[_charIndex..targetCharIndex]);
            _charIndex = targetCharIndex;
        }

        return _byteOffset;
    }
}
