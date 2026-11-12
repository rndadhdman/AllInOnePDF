using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.Core.Storage;

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string SourceFileName { get; set; } = "";
    public string? SourcePath { get; set; }
    public string SourceFormatId { get; set; } = "";
    public string TargetFormatId { get; set; } = "";
    public string? ThemeId { get; set; }
    public OptionValues ImportOptions { get; set; } = OptionValues.Empty;
    public OptionValues ExportOptions { get; set; } = OptionValues.Empty;
    public List<string> OutputPaths { get; set; } = [];
    public bool Success { get; set; }
    public string? Error { get; set; }
    public double ElapsedSeconds { get; set; }
    public long OutputBytes { get; set; }

    public string? PrimaryOutput => OutputPaths.FirstOrDefault();
}

/// <summary>Recent conversions, newest first. Capped so the file stays small.</summary>
public interface IConversionHistory
{
    Task<IReadOnlyList<HistoryEntry>> GetAsync(CancellationToken cancellationToken = default);
    Task AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default);
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    event Action? Changed;
}

public sealed class FileConversionHistory : IConversionHistory, IDisposable
{
    private const int MaxEntries = 200;
    private readonly JsonFileStore _store;
    // One writer at a time: read-modify-write must be atomic or jobs finishing together lose entries.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileConversionHistory(AppPaths paths)
    {
        _store = new JsonFileStore(paths.HistoryFile);
    }

    public event Action? Changed;

    public async Task<IReadOnlyList<HistoryEntry>> GetAsync(CancellationToken cancellationToken = default) =>
        await _store.ReadAsync<List<HistoryEntry>>(cancellationToken) ?? [];

    public Task AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default) =>
        MutateAsync(list =>
        {
            list.Insert(0, entry);
            if (list.Count > MaxEntries)
            {
                list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            }
        }, cancellationToken);

    public Task RemoveAsync(string id, CancellationToken cancellationToken = default) =>
        MutateAsync(list => list.RemoveAll(e => e.Id == id), cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(list => list.Clear(), cancellationToken);

    private async Task MutateAsync(Action<List<HistoryEntry>> mutate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var list = (await GetAsync(cancellationToken)).ToList();
            mutate(list);
            await _store.WriteAsync(list, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        _gate.Dispose();
        _store.Dispose();
    }

    public static HistoryEntry FromJob(ConversionJob job, string sourceFormatId)
    {
        var r = job.Result;
        return new HistoryEntry
        {
            Timestamp = job.CompletedAt ?? DateTimeOffset.Now,
            SourceFileName = job.SourceFileName,
            SourcePath = job.Request.SourcePath,
            SourceFormatId = sourceFormatId,
            TargetFormatId = job.Request.TargetFormatId,
            ThemeId = job.Request.ThemeId ?? job.Request.Theme?.Id,
            ImportOptions = job.Request.ImportOptions,
            ExportOptions = job.Request.ExportOptions,
            OutputPaths = r?.WrittenPaths.ToList() ?? [],
            Success = r?.Success ?? false,
            Error = r?.Error,
            ElapsedSeconds = r?.Elapsed.TotalSeconds ?? 0,
            OutputBytes = r?.Outputs.Sum(o => o.Length) ?? 0,
        };
    }
}
