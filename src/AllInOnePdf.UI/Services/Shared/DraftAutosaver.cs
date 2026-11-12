using System.Globalization;
using AllInOnePdf.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AllInOnePdf.UI.Services.Shared;

/// <summary>
/// Writes an editor's crash-recovery draft to <see cref="IDraftStore"/> a short while after the last change. Callers
/// pass a builder that only touches values captured at schedule time (it runs on a thread-pool thread). While a
/// write is running, a request that falls due is not started in parallel: it is written as soon as the running one
/// finishes. Thread-safe. Disposing flushes a pending draft in the background, so a window closed right after an
/// edit still leaves a draft behind.
/// </summary>
public sealed class DraftAutosaver : IDisposable
{
    /// <summary>Drafts older than this are deleted when a builder opens.</summary>
    public static readonly TimeSpan MaxDraftAge = TimeSpan.FromDays(30);

    private static readonly Lock PruneLock = new();
    private static DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    private readonly IDraftStore _store;
    private readonly TimeSpan _delay;
    private readonly ILogger? _logger;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _wait;
    private (string Key, Func<byte[]> Build)? _pending;
    private bool _due;
    private Task _pump = Task.CompletedTask;
    private bool _pumping;
    private bool _disposed;

    /// <summary>Creates an autosaver that writes <paramref name="delay"/> after the last <see cref="Schedule"/>.</summary>
    public DraftAutosaver(IDraftStore store, TimeSpan delay, ILogger? logger = null)
    {
        _store = store;
        _delay = delay;
        _logger = logger;
    }

    /// <summary>Asks for the draft <paramref name="key"/> to be written after the quiet period, replacing any earlier request.</summary>
    /// <param name="key">The draft key.</param>
    /// <param name="build">Produces the draft bytes; runs off the UI thread and must only use captured, unchanging values.</param>
    public void Schedule(string key, Func<byte[]> build)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(build);
        CancellationTokenSource wait;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _pending = (key, build);
            CancelWait();
            wait = new CancellationTokenSource();
            _wait = wait;
        }

        _ = WaitThenWriteAsync(wait);
    }

    /// <summary>Drops a pending request (a running write finishes).</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _pending = null;
            _due = false;
            CancelWait();
        }
    }

    /// <summary>Drops any pending request, waits for a running write, then deletes the draft <paramref name="key"/>.</summary>
    public async Task DiscardAsync(string key)
    {
        Task pump;
        lock (_lock)
        {
            _pending = null;
            _due = false;
            CancelWait();
            pump = _pump;
        }

        try
        {
            await pump.ConfigureAwait(false);
            await _store.DeleteAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "The draft {Key} could not be deleted.", key);
        }
    }

    /// <summary>Stops the timer and writes a pending draft right away (in the background).</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelWait();
            if (_pending is not null)
            {
                _due = true;
                StartPumpLocked();
            }
        }
    }

    /// <summary>Deletes drafts older than <see cref="MaxDraftAge"/> in the background, at most once an hour per process.</summary>
    public static void PruneInBackground(IDraftStore store, ILogger? logger = null)
    {
        lock (PruneLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastPrune < TimeSpan.FromHours(1))
            {
                return;
            }

            _lastPrune = now;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await store.DeleteOlderThanAsync(MaxDraftAge).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Old drafts could not be cleaned up.");
            }
        });
    }

    /// <summary>"a moment ago", "5 minutes ago", "3 hours ago" or a date and time for older drafts.</summary>
    public static string DescribeAge(DateTimeOffset written, DateTimeOffset now)
    {
        var age = now - written;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "a moment ago";
        }

        if (age < TimeSpan.FromHours(1))
        {
            var minutes = (int)age.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            var hours = (int)age.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        return written.ToLocalTime().ToString("d MMMM 'at' HH:mm", CultureInfo.CurrentCulture);
    }

    private async Task WaitThenWriteAsync(CancellationTokenSource wait)
    {
        try
        {
            await Task.Delay(_delay, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            wait.Dispose();
            return;
        }

        lock (_lock)
        {
            if (ReferenceEquals(_wait, wait))
            {
                _wait = null;
                _due = true;
                StartPumpLocked();
            }
        }

        wait.Dispose();
    }

    // Caller holds _lock. Starts the writer loop unless it is running (it then picks up the due request itself).
    private void StartPumpLocked()
    {
        if (_pumping)
        {
            return;
        }

        _pumping = true;
        _pump = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            (string Key, Func<byte[]> Build) next;
            lock (_lock)
            {
                if (!_due || _pending is not { } pending)
                {
                    _due = false;
                    _pumping = false;
                    return;
                }

                next = pending;
                _pending = null;
                _due = false;
            }

            try
            {
                var bytes = next.Build();
                await _store.WriteAsync(next.Key, bytes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "The draft {Key} could not be written.", next.Key);
            }
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
