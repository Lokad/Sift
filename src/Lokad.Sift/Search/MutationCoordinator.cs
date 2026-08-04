namespace Lokad.Sift;

internal sealed class MutationCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _idle = new(initialState: true);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _requests;
    private int _currentActivity;
    private bool _stopped;
    private bool _disposed;

    public ActivityStats GetActivityStats()
    {
        var isBusy = Volatile.Read(ref _requests) > 0;
        var currentActivity = (SiftActivity)Volatile.Read(ref _currentActivity);
        return new ActivityStats(isBusy, isBusy ? currentActivity : SiftActivity.Idle);
    }

    public async Task<TResult> Run<TResult>(
        SiftActivity activity,
        Func<CancellationToken, ValueTask<TResult>> mutation,
        CancellationToken cancellationToken)
    {
        using var request = BeginRequest();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _cancellation.Token);
        await _gate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _currentActivity, (int)activity);
            return await Task.Run(
                async () => await mutation(operationCancellation.Token).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _currentActivity, (int)SiftActivity.Idle);
            _gate.Release();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _cancellation.Cancel();
        }
    }

    public void WaitForIdle()
    {
        _idle.Wait();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        WaitForIdle();
        _cancellation.Dispose();
        _idle.Dispose();
        _gate.Dispose();
    }

    private IDisposable BeginRequest()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopped, typeof(Sift));
            if (++_requests == 1)
            {
                _idle.Reset();
            }
        }

        return new RequestScope(this);
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
