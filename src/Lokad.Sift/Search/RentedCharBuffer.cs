using System.Buffers;
using System.Text;

namespace Lokad.Sift.Search;

internal struct RentedCharBuffer : IDisposable
{
    private char[]? _buffer;
    private int _length;

    public static RentedCharBuffer Decode(ReadOnlySpan<byte> utf8, ref long decodedChars)
    {
        var charCount = Encoding.UTF8.GetCharCount(utf8);
        decodedChars += charCount;
        if (charCount == 0)
        {
            return default;
        }

        var buffer = ArrayPool<char>.Shared.Rent(charCount);
        var charsWritten = Encoding.UTF8.GetChars(utf8, buffer);
        return new RentedCharBuffer
        {
            _buffer = buffer,
            _length = charsWritten
        };
    }

    public ReadOnlySpan<char> AsSpan()
    {
        return _buffer is null ? [] : _buffer.AsSpan(0, _length);
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<char>.Shared.Return(_buffer);
            _buffer = null;
            _length = 0;
        }
    }
}
