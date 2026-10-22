namespace Lokad.Sift;

internal sealed class MutationCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _idle = new(initialState: true);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _requests;
    private SiftWriteActivity _currentActivity;
    private bool _stopped;
    private bool _disposed;

    public WriteActivityStats GetWriteActivityStats()
    {
        lock (_sync)
        {
            return new WriteActivityStats(_requests > 0, _currentActivity);
        }
    }

    public async Task<TResult> Run<TResult>(
        SiftWriteActivity activity,
        Func<CancellationToken, ValueTask<TResult>> mutation,
        CancellationToken cancellationToken)
    {
        ThrowIfExecutingMutation();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopped, typeof(SiftIndex));
            if (++_requests == 1)
            {
                _idle.Reset();
            }
        }

        using var request = new RequestScope(this);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _cancellation.Token);
        await _gate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _currentActivity = activity;
            }

            return await Task.Run(
                async () =>
                {
                    using var callbackScope = ReentrancyGuard.Enter(this);
                    return await mutation(operationCancellation.Token).ConfigureAwait(false);
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _currentActivity = SiftWriteActivity.Idle;
            }

            _gate.Release();
        }
    }

    private void Stop()
    {
        lock (_sync)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
        }

        _cancellation.Cancel();
    }

    private void WaitForIdle()
    {
        ThrowIfExecutingMutation();
        _idle.Wait();
    }

    public void Dispose()
    {
        ThrowIfExecutingMutation();
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            Stop();
        }
        finally
        {
            // Caller code can register throwing callbacks on a mutation token.
            // Cancellation still reaches every request; drain them before cleanup.
            WaitForIdle();
            _cancellation.Dispose();
            _idle.Dispose();
            _gate.Dispose();
        }
    }

    public void ThrowIfExecutingMutation()
    {
        if (ReentrancyGuard.IsActive(this))
        {
            throw new InvalidOperationException(
                "The index cannot be mutated, disposed, or queried by GetStorageStats/ShouldCompact from one of its active mutation callbacks.");
        }
    }

    private sealed class RequestScope(MutationCoordinator owner) : IDisposable
    {
        private MutationCoordinator? _owner = owner;

        public void Dispose()
        {
            var currentOwner = Interlocked.Exchange(ref _owner, null);
            if (currentOwner is null)
            {
                return;
            }

            lock (currentOwner._sync)
            {
                if (--currentOwner._requests == 0)
                {
                    currentOwner._idle.Set();
                }
            }
        }
    }
}
