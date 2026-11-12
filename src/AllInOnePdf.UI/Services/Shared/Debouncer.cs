namespace AllInOnePdf.UI.Services.Shared;

/// <summary>
/// Runs an async action once after a quiet period. <see cref="Debounce"/> restarts the wait on every call (slider
/// saves); <see cref="Coalesce"/> keeps the wait that is already running, so a burst of calls produces at most one
/// run per period (progress-driven re-renders). The latest action passed wins. Thread-safe; dispose to drop any
/// pending run. An action that already started is not cancelled by later calls.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly Action<Exception>? _onError;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _wait;
    private Func<CancellationToken, Task>? _pending;
    private bool _disposed;

    /// <param name="delay">Quiet period. Zero runs on the next turn of the scheduler (pure coalescing).</param>
    /// <param name="onError">Receives exceptions thrown by an action (they would otherwise be lost).</param>
    public Debouncer(TimeSpan delay, Action<Exception>? onError = null)
    {
        _delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        _onError = onError;
    }

    /// <summary>True while an action waits to run.</summary>
    public bool IsPending
    {
        get
        {
            lock (_lock)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>Schedules <paramref name="action"/>, restarting the wait.</summary>
    public void Debounce(Func<CancellationToken, Task> action) => Schedule(action, restart: true);

    /// <summary>Schedules <paramref name="action"/> without restarting a wait that is already running.</summary>
    public void Coalesce(Func<CancellationToken, Task> action) => Schedule(action, restart: false);

    /// <summary>Synchronous convenience overload of <see cref="Coalesce(Func{CancellationToken, Task})"/>.</summary>
    public void Coalesce(Action action) => Coalesce(_ =>
    {
        action();
        return Task.CompletedTask;
    });

    /// <summary>Runs the pending action now (if any) instead of waiting. Used to flush a save on dispose.</summary>
    public async Task FlushAsync()
    {
        Func<CancellationToken, Task>? action;
        lock (_lock)
        {
            action = _pending;
            _pending = null;
            CancelWait();
        }

        if (action is not null)
        {
            await RunAsync(action, CancellationToken.None);
        }
    }

    /// <summary>Drops the pending action.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _pending = null;
            CancelWait();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pending = null;
            CancelWait();
        }
    }

    private void Schedule(Func<CancellationToken, Task> action, bool restart)
    {
        ArgumentNullException.ThrowIfNull(action);
        CancellationTokenSource wait;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var waiting = _pending is not null;
            _pending = action;
            if (waiting && !restart)
            {
                return;
            }

            CancelWait();
            wait = new CancellationTokenSource();
            _wait = wait;
        }

        _ = WaitThenRunAsync(wait, wait.Token);
    }

    private async Task WaitThenRunAsync(CancellationTokenSource wait, CancellationToken token)
    {
        try
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, token).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            wait.Dispose();
            return;
        }

        Func<CancellationToken, Task>? action;
        lock (_lock)
        {
            if (token.IsCancellationRequested || !ReferenceEquals(_wait, wait))
            {
                action = null;
            }
            else
            {
                action = _pending;
                _pending = null;
                _wait = null;
            }
        }

        wait.Dispose();
        if (action is not null)
        {
            await RunAsync(action, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        try
        {
            await action(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
    }

    // Caller holds _lock.
    private void CancelWait()
    {
        var wait = _wait;
        _wait = null;
        if (wait is null)
        {
            return;
        }

        try
        {
            wait.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
