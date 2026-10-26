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
        var operationToken = operationCancellation.Token;
        request.ObserveCancellation(operationToken);
        await _gate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                async () =>
                {
                    if (!request.TryStart(activity)) throw new OperationCanceledException(operationToken);
                    using var callbackScope = ReentrancyGuard.Enter(this);
                    return await mutation(operationToken).ConfigureAwait(false);
                },
                operationToken).ConfigureAwait(false);
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
            // Cancelled requests may still resume their gate/task cleanup. They
            // own no mutation resources, but an acquired permit still needs release.
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
        private int _state; // 0 queued, 1 running, 2 settled
        private CancellationTokenRegistration _cancellation;

        public void ObserveCancellation(CancellationToken token) =>
            _cancellation = token.UnsafeRegister(static state => ((RequestScope)state!).CancelQueued(), this);

        public bool TryStart(SiftWriteActivity activity)
        {
            lock (owner._sync)
            {
                if (_state != 0 || owner._stopped) return false;
                _state = 1;
                owner._currentActivity = activity;
                return true;
            }
        }

        private void CancelQueued()
        {
            lock (owner._sync)
            {
                if (_state == 0) Settle();
            }
        }

        public void Dispose()
        {
            _cancellation.Dispose();
            lock (owner._sync)
            {
                if (_state != 2) Settle();
            }
        }

        private void Settle()
        {
            _state = 2;
            if (--owner._requests == 0) owner._idle.Set();
        }
    }
}
