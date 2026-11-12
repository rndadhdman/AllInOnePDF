using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AllInOnePdf.Core.Conversion;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>One queued conversion with live progress. UI binds to these.</summary>
public sealed class ConversionJob : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public ConversionJob(ConversionRequest request)
    {
        Request = request;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public ConversionRequest Request { get; }
    public JobStatus Status { get; internal set; } = JobStatus.Queued;
    public ConversionProgress Progress { get; internal set; } = new(ConversionStage.Queued, 0);
    public ConversionResult? Result { get; internal set; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? CompletedAt { get; internal set; }

    public string SourceFileName => Request.ResolvedFileName;
    public bool IsFinished => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;
    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    internal CancellationToken Token => _cts.Token;

    public void Cancel()
    {
        if (!IsFinished)
        {
            _cts.Cancel();
        }
    }

    public void Dispose() => _cts.Dispose();
}

/// <summary>
/// Runs conversions in the background with bounded parallelism and raises <see cref="Changed"/> on every
/// state change (marshal to the UI thread in Blazor with InvokeAsync).
/// </summary>
public sealed class ConversionQueue : IDisposable
{
    private readonly IConversionService _service;
    private readonly ILogger<ConversionQueue> _logger;
    private readonly ConcurrentQueue<ConversionJob> _pending = new();
    private readonly List<ConversionJob> _jobs = [];
    private readonly object _gate = new();
    private int _maxParallel;
    private int _active;
    private bool _disposed;

    public ConversionQueue(IConversionService service, ILogger<ConversionQueue> logger, int maxParallel = 2)
    {
        _service = service;
        _logger = logger;
        _maxParallel = Math.Clamp(maxParallel, 1, 8);
    }

    /// <summary>Raised when a job is added, progresses or finishes. May fire on a background thread.</summary>
    public event Action<ConversionJob>? Changed;

    /// <summary>How many conversions run at once (1–8). Can be changed at any time; queued jobs pick it up immediately.</summary>
    public int MaxParallel
    {
        get => _maxParallel;
        set
        {
            lock (_gate)
            {
                _maxParallel = Math.Clamp(value, 1, 8);
            }

            StartPending();
        }
    }

    public IReadOnlyList<ConversionJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return _jobs.ToList();
            }
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Count(j => !j.IsFinished);
            }
        }
    }

    public ConversionJob Enqueue(ConversionRequest request)
    {
        var job = new ConversionJob(request);
        lock (_gate)
        {
            _jobs.Add(job);
        }

        _pending.Enqueue(job);
        Changed?.Invoke(job);
        StartPending();
        return job;
    }

    public void ClearFinished()
    {
        List<ConversionJob> removed;
        lock (_gate)
        {
            removed = _jobs.Where(j => j.IsFinished).ToList();
            _jobs.RemoveAll(j => j.IsFinished);
        }

        foreach (var job in removed)
        {
            job.Dispose();
        }
    }

    public void Remove(ConversionJob job)
    {
        job.Cancel();
        lock (_gate)
        {
            _jobs.Remove(job);
        }

        if (job.IsFinished)
        {
            job.Dispose();
        }
    }

    public void CancelAll()
    {
        foreach (var job in Jobs)
        {
            job.Cancel();
        }
    }

    private void StartPending()
    {
        while (true)
        {
            ConversionJob? job;
            lock (_gate)
            {
                if (_disposed || _active >= _maxParallel || !_pending.TryDequeue(out job))
                {
                    return;
                }

                _active++;
            }

            var started = job;
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunAsync(started);
                }
                finally
                {
                    lock (_gate)
                    {
                        _active--;
                    }

                    StartPending();
                }
            });
        }
    }

    private async Task RunAsync(ConversionJob job)
    {
        if (job.IsCancellationRequested)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.Now;
            Changed?.Invoke(job);
            return;
        }

        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.Now;
        Changed?.Invoke(job);

        // Progress<T> posts its callbacks asynchronously, so a report can arrive after the job has finished; those
        // must not undo the final state set below.
        var progress = new Progress<ConversionProgress>(p =>
        {
            if (job.IsFinished)
            {
                return;
            }

            job.Progress = p;
            Changed?.Invoke(job);
        });

        try
        {
            var result = await _service.ConvertAsync(job.Request, progress, job.Token);
            job.Result = result;
            job.Status = result.Success ? JobStatus.Succeeded : result.Error == "Cancelled" ? JobStatus.Cancelled : JobStatus.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error running job {JobId}", job.Id);
            job.Result = new ConversionResult { Success = false, Elapsed = TimeSpan.Zero, Error = ex.Message, Exception = ex };
            job.Status = JobStatus.Failed;
        }

        job.CompletedAt = DateTimeOffset.Now;
        // The final progress is set here rather than left to the last asynchronous report, so a listener that sees
        // IsFinished always sees Overall == 1 as well.
        job.Progress = new ConversionProgress(job.Status switch
        {
            JobStatus.Succeeded => ConversionStage.Completed,
            JobStatus.Cancelled => ConversionStage.Cancelled,
            _ => ConversionStage.Failed,
        }, 1, job.Progress.Message);
        Changed?.Invoke(job);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        CancelAll();
    }
}
