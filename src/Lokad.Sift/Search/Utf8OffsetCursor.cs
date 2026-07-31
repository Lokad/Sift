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

        while (_charIndex < targetCharIndex)
        {
            var current = text[_charIndex];
            if (char.IsHighSurrogate(current) &&
                _charIndex + 1 < text.Length &&
                char.IsLowSurrogate(text[_charIndex + 1]))
            {
                if (_charIndex + 1 == targetCharIndex)
                {
                    // A UTF-16 match may split a scalar; both halves still share its UTF-8 boundary.
                    return _byteOffset;
                }

                _byteOffset += 4;
                _charIndex += 2;
                continue;
            }

            _byteOffset += current switch
            {
                <= '\u007F' => 1,
                <= '\u07FF' => 2,
                _ => 3
            };
            _charIndex++;
        }

        return _byteOffset;
    }
}
