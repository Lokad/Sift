namespace Lokad.Sift.Search;

internal sealed class BoundedLruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly int _maximumWeight;
    private readonly Func<TKey, int> _getWeight;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _recency = new();
    private readonly object _sync = new();
    private int _weight;

    public BoundedLruCache(
        int capacity,
        int maximumWeight,
        Func<TKey, int> getWeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWeight);
        _capacity = capacity;
        _maximumWeight = maximumWeight;
        _getWeight = getWeight;
        _entries = new Dictionary<TKey, LinkedListNode<Entry>>(capacity);
    }

    public int Weight
    {
        get
        {
            lock (_sync)
            {
                return _weight;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> create, out bool cacheHit)
    {
        Lazy<TValue> value;
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _recency.Remove(existing);
                _recency.AddFirst(existing);
                cacheHit = true;
                value = existing.Value.Value;
            }
            else
            {
                value = new Lazy<TValue>(
                    () => create(key),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                var weight = Math.Max(0, _getWeight(key));
                var node = _recency.AddFirst(new Entry(key, value, weight));
                _entries.Add(key, node);
                _weight = checked(_weight + weight);
                TrimCompletedValuesUnderLock();

                cacheHit = false;
            }
        }

        try
        {
            var result = value.Value;
            lock (_sync)
            {
                TrimCompletedValuesUnderLock();
            }

            return result;
        }
        catch
        {
            RemoveFaultedValue(key, value);
            throw;
        }
    }

    private void TrimCompletedValuesUnderLock()
    {
        while (_entries.Count > _capacity || _weight > _maximumWeight)
        {
            var expired = _recency.Last;
            while (expired is not null && !expired.Value.Value.IsValueCreated)
            {
                expired = expired.Previous;
            }

            if (expired is null)
            {
                // An in-flight factory is the coalescing point for its key. Temporarily
                // exceed the bound rather than evict it and duplicate expensive work.
                return;
            }

            _recency.Remove(expired);
            _entries.Remove(expired.Value.Key);
            _weight -= expired.Value.Weight;
        }
    }

    private void RemoveFaultedValue(TKey key, Lazy<TValue> value)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var current) &&
                ReferenceEquals(current.Value.Value, value))
            {
                _entries.Remove(key);
                _recency.Remove(current);
                _weight -= current.Value.Weight;
            }
        }
    }

    private sealed record Entry(TKey Key, Lazy<TValue> Value, int Weight);
}
