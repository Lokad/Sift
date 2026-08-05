using Lokad.Sift.LowLevel;
using Lokad.Sift.Storage;

namespace Lokad.Sift.Search;

// Keeps common path-prefix scopes as a range so a corpus-wide search does not
// allocate one uint per document before candidate generation even begins.
internal sealed class OrdinalSet
{
    private readonly uint[] _ordinals;
    private readonly DeadOrdinalSet _excludedOrdinals;
    private readonly int _excludedStart;
    private readonly int _excludedEnd;
    private readonly uint _startInclusive;
    private readonly uint _endExclusive;
    private readonly bool _isRange;

    private OrdinalSet(uint[] ordinals)
    {
        _ordinals = ordinals;
        _excludedOrdinals = DeadOrdinalSet.Empty;
        Count = ordinals.Length;
    }

    private OrdinalSet(uint startInclusive, uint endExclusive, DeadOrdinalSet excludedOrdinals)
    {
        _ordinals = [];
        _excludedOrdinals = excludedOrdinals;
        _startInclusive = startInclusive;
        _endExclusive = endExclusive;
        _isRange = true;
        _excludedStart = LowerBound(excludedOrdinals.Span, startInclusive);
        _excludedEnd = LowerBound(excludedOrdinals.Span, endExclusive);
        Count = checked((int)(endExclusive - startInclusive) - (_excludedEnd - _excludedStart));
    }

    public static OrdinalSet Empty { get; } = new([]);

    public int Count { get; }

    public bool CoversWholeSegment(int documentCount) =>
        _isRange &&
        _startInclusive == 0 &&
        _endExclusive == (uint)documentCount &&
        _excludedStart == _excludedEnd;

    public static OrdinalSet FromSorted(uint[] ordinals) =>
        ordinals.Length == 0 ? Empty : new OrdinalSet(ordinals);

    public static OrdinalSet FromRange(uint startInclusive, uint endExclusive, DeadOrdinalSet excludedOrdinals)
    {
        if (endExclusive < startInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(endExclusive));
        }

        return startInclusive == endExclusive
            ? Empty
            : new OrdinalSet(startInclusive, endExclusive, excludedOrdinals);
    }

    public Enumerator GetEnumerator() => new(this);

    public uint[] ToArray()
    {
        if (!_isRange)
        {
            return _ordinals.ToArray();
        }

        var result = new uint[Count];
        var index = 0;
        foreach (var ordinal in this)
        {
            result[index++] = ordinal;
        }

        return result;
    }

    public bool TryGetSpan(out ReadOnlySpan<uint> ordinals)
    {
        if (_isRange)
        {
            ordinals = default;
            return false;
        }

        ordinals = _ordinals;
        return true;
    }

    public void CopyTo(PooledBitSet destination)
    {
        foreach (var ordinal in this)
        {
            destination.Set((int)ordinal);
        }
    }

    private static int LowerBound(ReadOnlySpan<uint> values, uint target)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var mid = low + ((high - low) >> 1);
            if (values[mid] < target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    public struct Enumerator
    {
        private readonly OrdinalSet _set;
        private int _ordinalIndex;
        private int _excludedIndex;
        private uint _rangeOrdinal;

        internal Enumerator(OrdinalSet set)
        {
            _set = set;
            _ordinalIndex = -1;
            _excludedIndex = set._excludedStart;
            _rangeOrdinal = set._startInclusive;
            Current = 0;
        }

        public uint Current { get; private set; }

        public bool MoveNext()
        {
            if (!_set._isRange)
            {
                _ordinalIndex++;
                if (_ordinalIndex >= _set._ordinals.Length)
                {
                    return false;
                }

                Current = _set._ordinals[_ordinalIndex];
                return true;
            }

            while (_rangeOrdinal < _set._endExclusive)
            {
                var candidate = _rangeOrdinal++;
                if (_excludedIndex < _set._excludedEnd && _set._excludedOrdinals[_excludedIndex] == candidate)
                {
                    _excludedIndex++;
                    continue;
                }

                Current = candidate;
                return true;
            }

            return false;
        }
    }
}
