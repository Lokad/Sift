using System.Runtime.ExceptionServices;

namespace Lokad.Sift.Search;

// Index-owned binding shared by snapshots. Each operation pins only its own
// query entry, so a slow search cannot retain unrelated evicted patterns.
internal sealed class RegexExecutionContext
{
    private readonly ISiftRegexEngine? _engine;
    private readonly Dictionary<PreparedQueryKey, Entry> _queries = new();
    private readonly LinkedList<Entry> _recency = new();
    private readonly object _sync = new();
    private long _weight;
    private int _owners = 1;
    private int _operations;
    private static int _nativeEntries;
    private static long _nativePatternCharacters;

    internal static int NativeEntries => Volatile.Read(ref _nativeEntries);
    internal static int NativePatternCharacters => (int)Math.Min(int.MaxValue, Volatile.Read(ref _nativePatternCharacters));

    public RegexExecutionContext(ISiftRegexEngine? engine) => _engine = engine;

    public RegexExecutionContext AddOwner()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_owners == 0, this);
            _owners++;
            return this;
        }
    }

    public void ReleaseOwner()
    {
        Entry? release = null;
        lock (_sync)
        {
            _owners--;
            if (_owners == 0 && _operations == 0) Clear(ref release);
        }
        Release(release);
    }

    public Operation EnterOperation()
    {
        var operation = new Operation(this);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_owners == 0, this);
            _operations++;
        }
        return operation;
    }

    public PreparedSearchQuery Prepare(Operation operation, SearchQuery query, CancellationToken cancellationToken,
        out bool cacheHit)
    {
        var key = PreparedQueryKey.FromQuery(query);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entry entry;
            lock (_sync)
            {
                cacheHit = _queries.TryGetValue(key, out entry!);
                if (!cacheHit)
                {
                    entry = new Entry(key, new QueryCell(token =>
                        SearchQueryPreparer.Create(key, _engine, token.ThrowIfCancellationRequested)));
                    entry.Node = _recency.AddFirst(entry);
                    _queries.Add(key, entry);
                    _weight += entry.Weight;
                }
                else
                {
                    _recency.Remove(entry.Node!);
                    _recency.AddFirst(entry.Node!);
                }
                operation.Attach(entry);
                entry.Users++;
            }
            Entry? release = null;
            try
            {
                var result = entry.Value.GetValue(cancellationToken);
                lock (_sync)
                {
                    if (!entry.Counted && entry.Cached && result.RegexPlan.UsesUtf8Regex)
                    {
                        entry.Counted = true;
                        Interlocked.Increment(ref _nativeEntries);
                        Interlocked.Add(ref _nativePatternCharacters, entry.Weight);
                    }
                    Trim(ref release);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (entry.Value.IsFaulted &&
                entry.Value.CompilationToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // A waiter whose own token is still live retries a factory that
                // was cancelled by its originating caller, using its own token.
                lock (_sync)
                {
                    if (entry.Cached) Remove(entry, ref release);
                    operation.Detach();
                    if (--entry.Users == 0) QueueRelease(entry, ref release);
                }
            }
            catch
            {
                lock (_sync)
                {
                    if (entry.Value.IsFaulted && entry.Cached) Remove(entry, ref release);
                }
                throw;
            }
            finally { Release(release); }
        }
    }

#if SIFT_INTERNAL_VERIFICATION
    internal int ActiveQueryUsers
    {
        get { lock (_sync) return _queries.Values.Sum(entry => entry.Users); }
    }
#endif

    private void Trim(ref Entry? release)
    {
        while (_queries.Count > SearchQueryPreparer.CacheCapacity ||
               _weight > SearchQueryPreparer.CacheCharacterCapacity)
        {
            var node = _recency.Last;
            while (node is not null && !node.Value.Value.IsValueCreated) node = node.Previous;
            // Preserve in-flight factories as the coalescing points for their keys.
            if (node is null) return;
            Remove(node.Value, ref release);
        }
    }

    private void Remove(Entry entry, ref Entry? release)
    {
        _queries.Remove(entry.Key);
        _recency.Remove(entry.Node!);
        _weight -= entry.Weight;
        entry.Cached = false;
        if (entry.Counted)
        {
            Interlocked.Decrement(ref _nativeEntries);
            Interlocked.Add(ref _nativePatternCharacters, -entry.Weight);
        }
        if (entry.Users == 0) QueueRelease(entry, ref release);
    }

    private void Clear(ref Entry? release)
    {
        while (_recency.Last is { } node) Remove(node.Value, ref release);
    }

    private static void QueueRelease(Entry entry, ref Entry? release)
    {
        entry.ReleaseNext = release;
        release = entry;
    }

    private static void Release(Entry? release)
    {
        while (release is not null)
        {
            var entry = release;
            release = entry.ReleaseNext;
            entry.ReleaseNext = null;
            entry.Value.Dispose();
        }
    }

    private void LeaveOperation(Entry? entry)
    {
        Entry? release = null;
        lock (_sync)
        {
            if (entry is not null && --entry.Users == 0 && !entry.Cached)
                QueueRelease(entry, ref release);
            _operations--;
            if (_owners == 0 && _operations == 0) Clear(ref release);
        }
        Release(release);
    }

    // The elected caller compiles synchronously. Other callers can stop waiting
    // on their own cancellation/deadline without cancelling or evicting that work.
    internal sealed class QueryCell(Func<CancellationToken, PreparedSearchQuery> factory) : IDisposable
    {
        private Func<CancellationToken, PreparedSearchQuery>? _factory = factory;
        private readonly ManualResetEventSlim _completed = new();
        private PreparedSearchQuery? _value;
        private ExceptionDispatchInfo? _failure;
        private int _state; // 0 unstarted, 1 compiling, 2 ready, 3 faulted
        private int _compilingThread;
        public bool IsValueCreated => Volatile.Read(ref _state) == 2;
        public bool IsFaulted => Volatile.Read(ref _state) == 3;
        public CancellationToken CompilationToken { get; private set; }

        public PreparedSearchQuery GetValue(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            {
                CompilationToken = cancellationToken;
                _compilingThread = Environment.CurrentManagedThreadId;
                try
                {
                    _value = _factory!(cancellationToken);
                    // A ready cache cell retains neither the checkpoint nor its caller's source.
                    CompilationToken = default;
                    Volatile.Write(ref _state, 2);
                }
                catch (Exception exception)
                {
                    _failure = ExceptionDispatchInfo.Capture(exception);
                    Volatile.Write(ref _state, 3);
                }
                finally
                {
                    _factory = null;
                    _compilingThread = 0;
                    _completed.Set();
                }
            }
            else if (Volatile.Read(ref _state) == 1)
            {
                if (Volatile.Read(ref _compilingThread) == Environment.CurrentManagedThreadId)
                    throw new InvalidOperationException("A regex engine cannot recursively prepare the same query.");
                _completed.Wait(cancellationToken);
            }
            _failure?.Throw();
            return _value!;
        }

        public void Dispose()
        {
            try { if (IsValueCreated) _value!.DisposeCompiledRegex(); }
            finally { CompilationToken = default; _completed.Dispose(); }
        }
    }

    internal sealed class Entry(PreparedQueryKey key, QueryCell value)
    {
        public PreparedQueryKey Key { get; } = key;
        public int Weight { get; } = key.Pattern.Length + (key.WholeWord ? 8 : 0) + (key.WholeLine ? 6 : 0);
        public QueryCell Value { get; } = value;
        public LinkedListNode<Entry>? Node;
        public Entry? ReleaseNext;
        public int Users;
        public bool Cached = true;
        public bool Counted;
    }

    internal sealed class Operation(RegexExecutionContext context) : IDisposable
    {
        private RegexExecutionContext? _context = context;
        private Entry? _entry;
        internal void Attach(Entry entry)
        {
            if (_entry is not null) throw new InvalidOperationException("A query operation is already bound.");
            _entry = entry;
        }
        internal void Detach() => _entry = null;
        public void Dispose() => Interlocked.Exchange(ref _context, null)?.LeaveOperation(_entry);
    }
}
