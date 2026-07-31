namespace Lokad.Sift.Search;

internal sealed class BoundedLruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _recency = new();
    private readonly object _sync = new();

    public BoundedLruCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _entries = new Dictionary<TKey, LinkedListNode<Entry>>(capacity);
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
                var node = _recency.AddFirst(new Entry(key, value));
                _entries.Add(key, node);
                if (_entries.Count > _capacity)
                {
                    var expired = _recency.Last ??
                        throw new InvalidOperationException("The cache recency list is unexpectedly empty.");
                    _recency.RemoveLast();
                    _entries.Remove(expired.Value.Key);
                }

                cacheHit = false;
            }
        }

        try
        {
            return value.Value;
        }
        catch
        {
            RemoveFaultedValue(key, value);
            throw;
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
            }
        }
    }

    private sealed record Entry(TKey Key, Lazy<TValue> Value);
}
