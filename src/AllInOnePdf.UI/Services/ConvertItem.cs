using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.UI.Services;

/// <summary>Where a file in the Convert workspace is in its life cycle.</summary>
public enum ConvertItemState
{
    /// <summary>Added and convertible; not converted yet (or changed since the last run).</summary>
    Ready,
    /// <summary>No importer can read this file (or its importer is only planned).</summary>
    Unsupported,
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>What to do when an output file already exists.</summary>
public enum OverwritePolicy
{
    /// <summary>Ask before replacing existing files.</summary>
    Ask,
    /// <summary>Keep both: the new file gets a " (2)" suffix.</summary>
    KeepBoth,
    /// <summary>Replace existing files silently.</summary>
    Replace,
}

/// <summary>Name, size and type of one converted file (kept after the bytes are released).</summary>
public sealed record ConvertOutputInfo(string FileName, long Length, string MediaType, bool IsPrimary);

/// <summary>
/// What the last finished run of a file produced. A snapshot taken from the <see cref="ConversionJob"/>, so the
/// job (and the source bytes its request holds) can leave the shared queue as soon as it finishes. Output bytes are
/// kept only for browser downloads and only until they were downloaded.
/// </summary>
public sealed class ConvertOutcome
{
    private IReadOnlyList<OutputFile>? _downloads;

    internal ConvertOutcome(ConversionJob job, bool keepDownloads, bool includeErrorDetails, bool sourceWasEmpty = false)
    {
        var result = job.Result;
        Status = job.Status;
        Elapsed = result?.Elapsed ?? TimeSpan.Zero;
        Error = result?.Error;
        ErrorDetails = includeErrorDetails && result?.Exception is { } ex
            ? $"{ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}"
            : null;
        Warnings = result?.Warnings ?? [];
        if (sourceWasEmpty && job.Status == JobStatus.Succeeded)
        {
            // A 0-byte source "converts" to an empty document; say so instead of reporting a plain success.
            Warnings = [EmptySourceWarning, .. Warnings];
        }
        WrittenPaths = result?.WrittenPaths ?? [];
        Files = result?.Outputs.Select(o => new ConvertOutputInfo(o.FileName, o.Length, o.MediaType, o.IsPrimary)).ToList() ?? [];
        Document = result?.Document;
        _downloads = keepDownloads && result is { Success: true } ? result.Outputs : null;
    }

    /// <summary>Shown on a finished row whose source file had no content.</summary>
    public const string EmptySourceWarning = "The source file is empty (0 bytes), so the result has no content.";

    public JobStatus Status { get; }

    public TimeSpan Elapsed { get; }

    /// <summary>User-facing error message (never a stack trace).</summary>
    public string? Error { get; }

    /// <summary>Exception type, message and stack trace; only on the desktop host (null on the web).</summary>
    public string? ErrorDetails { get; }

    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Files written to disk (desktop), primary first.</summary>
    public IReadOnlyList<string> WrittenPaths { get; }

    public string? PrimaryPath => WrittenPaths.Count > 0 ? WrittenPaths[0] : null;

    public IReadOnlyList<ConvertOutputInfo> Files { get; }

    public ConvertOutputInfo? PrimaryFile => Files.FirstOrDefault(f => f.IsPrimary) ?? (Files.Count > 0 ? Files[0] : null);

    public long TotalBytes => Files.Sum(f => f.Length);

    /// <summary>The imported document, for "Read".</summary>
    public Document? Document { get; private set; }

    /// <summary>Output bytes waiting to be downloaded (web only); null once downloaded or released.</summary>
    public IReadOnlyList<OutputFile>? Downloads => _downloads;

    /// <summary>True after the outputs were downloaded and their bytes released.</summary>
    public bool Downloaded { get; private set; }

    internal long HeldBytes => _downloads?.Sum(o => o.Length) ?? 0;

    internal void MarkDownloaded()
    {
        _downloads = null;
        Downloaded = true;
    }

    internal void Release()
    {
        _downloads = null;
        Document = null;
    }
}

/// <summary>
/// One file in the Convert workspace: its source (path on desktop; bytes or a spooled temp file for uploads), chosen
/// target, options per side, the running job and the outcome of the last run. Lives in the scoped
/// <see cref="ConvertWorkspace"/>.
/// </summary>
public sealed class ConvertItem
{
    private readonly Dictionary<string, OptionValues> _exportOptions = new(StringComparer.OrdinalIgnoreCase);
    private int _finishHandled;

    internal ConvertItem(string fileName, string? sourcePath, byte[]? content, string? spoolPath, long size, IImporter? importer)
    {
        FileName = fileName;
        SourcePath = sourcePath;
        Content = content;
        SpoolPath = spoolPath;
        Size = size;
        Importer = importer;
        ImportOptions = importer is null ? new OptionValues() : OptionValues.FromDefaults(importer.Options);
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>File name with extension, no directory.</summary>
    public string FileName { get; }

    /// <summary>Absolute path when the file was picked from disk (desktop); null for uploads and drops.</summary>
    public string? SourcePath { get; }

    /// <summary>In-memory content of a small upload or drop; null once spooled or released.</summary>
    public byte[]? Content { get; internal set; }

    /// <summary>Temporary copy of an upload under the app cache (large uploads, or small ones after use).</summary>
    public string? SpoolPath { get; internal set; }

    /// <summary>True when the file came from an upload or drop rather than a path on disk.</summary>
    public bool IsUpload => SourcePath is null;

    /// <summary>True while the source can still be read (path, bytes or spooled copy).</summary>
    public bool HasSource => SourcePath is not null || Content is not null || SpoolPath is not null;

    public long Size { get; }

    public IImporter? Importer { get; }

    public DateTimeOffset AddedAt { get; } = DateTimeOffset.Now;

    public bool IsSupported => Importer is { Status: not ConverterStatus.Planned };

    public string Extension => Path.GetExtension(FileName).ToUpperInvariant().TrimStart('.');

    /// <summary>Selected target format id, or null when no target is available.</summary>
    public string? TargetFormatId { get; internal set; }

    /// <summary>True while the target is the one remembered from the last file of the same type (not picked for this file).</summary>
    public bool TargetRemembered { get; internal set; }

    public OptionValues ImportOptions { get; internal set; }

    /// <summary>Export options for the current target. Options are kept per target so switching back restores them.</summary>
    public OptionValues ExportOptions => TargetFormatId is null ? OptionValues.Empty : GetExportOptions(TargetFormatId);

    /// <summary>The job in the queue while converting (null before the first run and once a run is handled).</summary>
    public ConversionJob? Job { get; internal set; }

    /// <summary>Outcome of the last finished run (null when never run, or reset by a change of target or options).</summary>
    public ConvertOutcome? Outcome { get; internal set; }

    /// <summary>When the latest run was queued (for the batch summary).</summary>
    public DateTimeOffset? LastRunAt { get; internal set; }

    /// <summary>Set when the user expands error details; kept here so it survives re-renders.</summary>
    public bool ShowDetails { get; set; }

    public ConvertItemState State
    {
        get
        {
            if (!IsSupported)
            {
                return ConvertItemState.Unsupported;
            }

            var status = Job?.Status ?? Outcome?.Status;
            return status switch
            {
                null => ConvertItemState.Ready,
                JobStatus.Queued => ConvertItemState.Queued,
                JobStatus.Running => ConvertItemState.Running,
                JobStatus.Succeeded => ConvertItemState.Succeeded,
                JobStatus.Failed => ConvertItemState.Failed,
                _ => ConvertItemState.Cancelled,
            };
        }
    }

    public bool IsBusy => State is ConvertItemState.Queued or ConvertItemState.Running;

    public bool IsFinished => State is ConvertItemState.Succeeded or ConvertItemState.Failed or ConvertItemState.Cancelled;

    internal Func<string, OptionValues>? ExportOptionsFactory { get; set; }

    internal OptionValues GetExportOptions(string targetFormatId)
    {
        if (!_exportOptions.TryGetValue(targetFormatId, out var values))
        {
            values = ExportOptionsFactory?.Invoke(targetFormatId) ?? new OptionValues();
            _exportOptions[targetFormatId] = values;
        }

        return values;
    }

    internal void SetExportOptions(string targetFormatId, OptionValues values) => _exportOptions[targetFormatId] = values;

    /// <summary>Returns true exactly once per job, so finish handling (history, toasts) never runs twice.</summary>
    internal bool TryMarkFinishHandled() => Interlocked.Exchange(ref _finishHandled, 1) == 0;

    internal void ResetFinishHandled() => Interlocked.Exchange(ref _finishHandled, 0);
}
