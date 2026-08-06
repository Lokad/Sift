using System.Buffers;
using System.Numerics;

namespace Lokad.Sift.LowLevel;

internal ref struct PooledBitSet
{
    private readonly uint[] _words;
    private readonly int _count;
    public int BitCount { get; }

    public PooledBitSet(int bitCount)
    {
        BitCount = bitCount;
        _count = (bitCount + 31) >> 5;
        _words = ArrayPool<uint>.Shared.Rent(_count);
        Array.Clear(_words, 0, _count);
    }

    public void Set(int bit)
    {
        _words[bit >> 5] |= 1u << (bit & 31);
    }

    public bool Contains(int bit)
    {
        return (_words[bit >> 5] & (1u << (bit & 31))) != 0;
    }

    public void And(PooledBitSet other)
    {
        for (var i = 0; i < _count; i++)
        {
            _words[i] &= other._words[i];
        }
    }

    public void Or(PooledBitSet other)
    {
        for (var i = 0; i < _count; i++)
        {
            _words[i] |= other._words[i];
        }
    }

    public void Clear()
    {
        Array.Clear(_words, 0, _count);
    }

    public int Count()
    {
        var total = 0;
        for (var i = 0; i < _count; i++)
        {
            total += BitOperations.PopCount(_words[i]);
        }

        return total;
    }

    public uint[] ToOrdinals()
    {
        var result = new uint[Count()];
        var cursor = 0;
        for (var wordIndex = 0; wordIndex < _count; wordIndex++)
        {
            var word = _words[wordIndex];
            while (word != 0)
            {
                var bit = BitOperations.TrailingZeroCount(word);
                result[cursor++] = (uint)((wordIndex << 5) + bit);
                word &= word - 1;
            }
        }

        return result;
    }

    public void Dispose()
    {
        Array.Clear(_words, 0, _count);
        ArrayPool<uint>.Shared.Return(_words);
    }
}
