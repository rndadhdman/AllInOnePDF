using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.Extensions.Logging;

namespace AllInOnePdf.UI.Services;

/// <summary>Counts for the summary line ("3 of 5 converted · 1 failed · 1 converting"). Each file is in one bucket.</summary>
public readonly record struct ConvertBatchSummary(int Succeeded, int Failed, int Cancelled, int Running, int Queued)
{
    public int Total => Succeeded + Failed + Cancelled + Running + Queued;
    public int Finished => Succeeded + Failed + Cancelled;
    public int Active => Running + Queued;
    public bool HasJobs => Total > 0;
    public bool IsRunning => Active > 0;
}

/// <summary>Payload for <see cref="ConvertWorkspace.ItemFinished"/>.</summary>
public sealed record ConvertItemFinished(ConvertItem Item, ConversionJob Job);

/// <summary>
/// Payload for <see cref="ConvertWorkspace.BatchFinished"/>: the files of the batch that just ended (started when
/// nothing was converting) and the last one to finish.
/// </summary>
public sealed record ConvertBatchFinished(ConvertBatchSummary Summary, ConvertItem LastItem, ConversionJob LastJob);

/// <summary>
/// Per-session (scoped) state of the Convert page: the file list, targets, options and output settings.
/// It survives navigation, owns the subscription to the shared <see cref="ConversionQueue"/> and writes
/// history entries when jobs finish, so work completes correctly even when the user is on another page.
/// Memory: uploads count against a per-workspace budget (<see cref="UploadLimits"/>); large ones are spooled to a
/// temporary file; finished jobs leave the queue at once, and output bytes are dropped once written (desktop) or
/// downloaded (web).
/// </summary>
public sealed class ConvertWorkspace : IDisposable
{
    /// <summary>Largest file accepted from uploads and drops.</summary>
    public const long MaxUploadBytes = UploadLimits.MaxUploadBytes;

    private static readonly TimeSpan StaleSpoolAge = TimeSpan.FromDays(1);

    private readonly FormatRegistry _registry;
    private readonly ConversionQueue _queue;
    private readonly IConversionHistory _history;
    private readonly IPreferencesService _preferences;
    private readonly IThemeStore _themes;
    private readonly IHostServices _host;
    private readonly AppState _appState;
    private readonly AppPaths _paths;
    private readonly ILogger<ConvertWorkspace> _logger;
    private readonly List<ConvertItem> _items = [];
    private readonly Dictionary<string, string> _lastTargetBySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly string _spoolDirectory;
    private string? _defaultThemeId;
    private bool _initialized;
    private DateTimeOffset _batchStart = DateTimeOffset.MinValue;
    private int _batchAnnounced;
    private bool _disposed;

    public ConvertWorkspace(
        FormatRegistry registry,
        ConversionQueue queue,
        IConversionHistory history,
        IPreferencesService preferences,
        IThemeStore themes,
        IHostServices host,
        AppState appState,
        AppPaths paths,
        ConversionQueueTuner queueTuner,
        ILogger<ConvertWorkspace> logger)
    {
        _registry = registry;
        _queue = queue;
        _history = history;
        _preferences = preferences;
        _themes = themes;
        _host = host;
        _appState = appState;
        _paths = paths;
        _logger = logger;
        _spoolDirectory = Path.Combine(paths.CacheDirectory, "uploads", Guid.NewGuid().ToString("N"));
        queueTuner.Apply();
        _queue.Changed += OnJobChanged;
        _themes.Changed += OnThemesChanged;
    }

    /// <summary>Raised when the list or any job changes. May fire on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Raised once per job when it finishes (after history is written). May fire on a background thread.</summary>
    public event Action<ConvertItemFinished>? ItemFinished;

    /// <summary>Raised once when the last file of a batch finishes. May fire on a background thread.</summary>
    public event Action<ConvertBatchFinished>? BatchFinished;

    public FormatRegistry Registry => _registry;

    public IReadOnlyList<ConvertItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToList();
            }
        }
    }

    public bool HasItems
    {
        get
        {
            lock (_gate)
            {
                return _items.Count > 0;
            }
        }
    }

    /// <summary>True when outputs are written to disk (desktop); false when they stay in memory for download (web).</summary>
    public bool WritesToDisk => _host.SupportsNativeDialogs;

    /// <summary>Output folder used when <see cref="SaveNextToSource"/> is off or the file has no path.</summary>
    public string? OutputDirectory { get; private set; }

    public bool SaveNextToSource => _preferences.Current.SaveNextToSource;

    public bool OpenAfterConvert => _preferences.Current.OpenAfterConvert;

    public bool RevealAfterConvert => _preferences.Current.RevealAfterConvert;

    /// <summary>What happens when an output already exists (<see cref="UserPreferences.OverwritePolicy"/>).</summary>
    public OverwritePolicy Overwrite => _preferences.Current.OverwritePolicy;

    /// <summary>"Add folder" also looks in sub-folders.</summary>
    public bool IncludeSubfolders { get; set; }

    /// <summary>Bytes of uploaded sources this workspace holds (in memory and spooled).</summary>
    public long UploadedBytes
    {
        get
        {
            lock (_gate)
            {
                return _items.Where(i => i.IsUpload).Sum(i => i.Content is not null || i.SpoolPath is not null ? i.Size : 0);
            }
        }
    }

    private long MemoryBytes
    {
        get
        {
            lock (_gate)
            {
                return _items.Sum(i => (i.Content?.LongLength ?? 0) + (i.Outcome?.HeldBytes ?? 0));
            }
        }
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _preferences.LoadAsync(cancellationToken);
        OutputDirectory ??= _appState.LastOutputDirectory ?? _preferences.Current.DefaultOutputDirectory;
        await RefreshDefaultThemeAsync(cancellationToken);
        _initialized = true;
        _ = Task.Run(CleanStaleSpoolDirectories, CancellationToken.None);
    }

    // ---- Adding and removing files --------------------------------------------------------------

    /// <summary>Adds a file from disk (desktop). Returns the existing item when the path is already in the list.</summary>
    public ConvertItem AddPath(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            var existing = _items.FirstOrDefault(i => i.SourcePath is not null && FilePaths.AreEqual(i.SourcePath, full));
            if (existing is not null)
            {
                return existing;
            }
        }

        long size = 0;
        try
        {
            size = new FileInfo(full).Length;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read size of {Path}", full);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not read size of {Path}", full);
        }

        var item = Create(Path.GetFileName(full), full, null, null, size);
        AddCore(item);
        return item;
    }

    /// <summary>
    /// Why an upload of <paramref name="size"/> bytes cannot be added (per-file limit or the workspace budget), or
    /// null when it fits.
    /// </summary>
    public string? CheckUpload(long size)
    {
        if (size > UploadLimits.MaxUploadBytes)
        {
            return $"it is larger than {UploadLimits.Describe(UploadLimits.MaxUploadBytes)}";
        }

        if (UploadedBytes + size > UploadLimits.MaxWorkspaceUploadBytes)
        {
            return $"this list already holds close to {UploadLimits.Describe(UploadLimits.MaxWorkspaceUploadBytes)} of uploads. Remove some files first";
        }

        return null;
    }

    /// <summary>True when an upload of this size should go straight to a temporary file instead of memory.</summary>
    public bool ShouldSpool(long size) =>
        size > UploadLimits.SpoolThresholdBytes || MemoryBytes + size > UploadLimits.MaxWorkspaceMemoryBytes;

    /// <summary>Adds an uploaded or dropped file held in memory (small files and the sample).</summary>
    public ConvertItem AddContent(string fileName, byte[] content)
    {
        var item = Create(Path.GetFileName(fileName), null, content, null, content.LongLength);
        AddCore(item);
        return item;
    }

    /// <summary>
    /// Adds an upload by copying <paramref name="content"/> to a temporary file under the app cache, so large uploads
    /// never sit in memory. The file is deleted when the row is removed or the workspace ends.
    /// </summary>
    public async Task<ConvertItem> AddSpooledAsync(string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(fileName);
        var path = NewSpoolPath(name);
        try
        {
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await content.CopyToAsync(target, cancellationToken);
            }

            var item = Create(name, null, null, path, new FileInfo(path).Length);
            AddCore(item);
            return item;
        }
        catch
        {
            TryDeleteSpool(path);
            throw;
        }
    }

    /// <summary>Adds every importable file in a folder. Returns how many were added.</summary>
    public int AddFolder(string folder, bool recursive)
    {
        var option = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", option).OrderBy(f => f, FilePaths.Comparer))
        {
            if (!_registry.CanOpen(file))
            {
                continue;
            }

            var before = Items.Count;
            AddPath(file);
            if (Items.Count > before)
            {
                count++;
            }

            if (count >= 500)
            {
                break;
            }
        }

        return count;
    }

    /// <summary>Re-adds a history entry with its options. Returns null when the source file no longer exists.</summary>
    public ConvertItem? AddFromHistory(HistoryEntry entry)
    {
        if (string.IsNullOrEmpty(entry.SourcePath) || !File.Exists(entry.SourcePath))
        {
            return null;
        }

        var item = AddPath(entry.SourcePath);
        if (item.IsBusy)
        {
            return item;
        }

        if (item.Importer is not null)
        {
            item.ImportOptions = entry.ImportOptions.Clone().WithDefaults(item.Importer.Options);
        }

        var exporter = _registry.FindExporter(entry.TargetFormatId);
        if (exporter is not null && exporter.Status != ConverterStatus.Planned && GetTargets(item).Contains(exporter))
        {
            item.TargetFormatId = exporter.Format.Id;
            item.TargetRemembered = false;
            var values = entry.ExportOptions.Clone().WithDefaults(exporter.Options);
            var themeKey = ThemeOptionKey(exporter);
            if (themeKey is not null)
            {
                // "Repeat with the same options" pins the theme that was used, as an explicit choice.
                if (!string.IsNullOrEmpty(entry.ThemeId))
                {
                    values.Set(themeKey, entry.ThemeId);
                }
                else
                {
                    values.Remove(themeKey);
                }
            }

            item.SetExportOptions(exporter.Format.Id, values);
        }

        ResetOutcome(item);
        RaiseChanged();
        return item;
    }

    public void Remove(ConvertItem item)
    {
        lock (_gate)
        {
            _items.Remove(item);
        }

        if (item.Job is { } job)
        {
            _queue.Remove(job);
            item.Job = null;
        }

        ReleaseSource(item);
        item.Outcome?.Release();
        RaiseChanged();
    }

    /// <summary>Removes every file that is not converting right now.</summary>
    public void Clear() => RemoveWhere(i => !i.IsBusy);

    /// <summary>Removes files that converted successfully.</summary>
    public void ClearCompleted() => RemoveWhere(i => i.State == ConvertItemState.Succeeded);

    private void RemoveWhere(Func<ConvertItem, bool> predicate)
    {
        List<ConvertItem> removed;
        lock (_gate)
        {
            removed = _items.Where(predicate).ToList();
            _items.RemoveAll(i => removed.Contains(i));
        }

        foreach (var item in removed)
        {
            if (item.Job is { } job)
            {
                _queue.Remove(job);
                item.Job = null;
            }

            ReleaseSource(item);
            item.Outcome?.Release();
        }

        RaiseChanged();
    }

    // ---- Targets and options --------------------------------------------------------------------

    public IReadOnlyList<IExporter> GetTargets(ConvertItem item) =>
        item.IsSupported ? _registry.GetTargetsFor(item.FileName) : [];

    public IExporter? GetExporter(ConvertItem item) =>
        item.TargetFormatId is null ? null : _registry.FindExporter(item.TargetFormatId);

    public void SetTarget(ConvertItem item, string targetFormatId)
    {
        var exporter = _registry.FindExporter(targetFormatId);
        if (exporter is null || exporter.Status == ConverterStatus.Planned || item.IsBusy)
        {
            return;
        }

        var changed = !string.Equals(item.TargetFormatId, exporter.Format.Id, StringComparison.OrdinalIgnoreCase);
        item.TargetFormatId = exporter.Format.Id;
        item.TargetRemembered = false;
        if (item.Importer is not null)
        {
            lock (_gate)
            {
                _lastTargetBySource[item.Importer.Format.Id] = exporter.Format.Id;
            }
        }

        if (changed)
        {
            // A different target makes the previous result stale: the row is Ready again.
            ResetOutcome(item);
        }

        RaiseChanged();
    }

    /// <summary>Targets every supported file can use, for the bulk "Set target for all" action.</summary>
    public IReadOnlyList<IExporter> CommonTargets()
    {
        var supported = Items.Where(i => i.IsSupported).ToList();
        if (supported.Count == 0)
        {
            return [];
        }

        IEnumerable<IExporter> common = GetTargets(supported[0]);
        foreach (var item in supported.Skip(1))
        {
            var targets = GetTargets(item);
            common = common.Where(targets.Contains);
        }

        return common.ToList();
    }

    /// <summary>Union of targets across files (so "PDF" shows even if one file is a PDF), PDF first.</summary>
    public IReadOnlyList<IExporter> AllTargets() =>
        Items.Where(i => i.IsSupported)
            .SelectMany(GetTargets)
            .Distinct()
            .OrderBy(e => e.Status)
            .ThenBy(e => e.Format.Id == KnownFormats.Pdf.Id ? 0 : 1)
            .ThenBy(e => e.Format.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Sets the target on every file that offers it. Returns how many changed.</summary>
    public int SetTargetForAll(string targetFormatId)
    {
        var count = 0;
        foreach (var item in Items.Where(i => i.IsSupported && !i.IsBusy))
        {
            if (GetTargets(item).Any(e => string.Equals(e.Format.Id, targetFormatId, StringComparison.OrdinalIgnoreCase) && e.Status != ConverterStatus.Planned))
            {
                SetTarget(item, targetFormatId);
                count++;
            }
        }

        return count;
    }

    public void ResetOptions(ConvertItem item)
    {
        if (item.Importer is not null)
        {
            item.ImportOptions = OptionValues.FromDefaults(item.Importer.Options);
        }

        if (item.TargetFormatId is not null)
        {
            item.SetExportOptions(item.TargetFormatId, CreateExportOptions(item.TargetFormatId));
        }

        OptionsChanged(item);
    }

    /// <summary>
    /// Call after editing an item's options in place (the options drawer). A finished row goes back to Ready and its
    /// old result is cleared, because it no longer matches the options.
    /// </summary>
    public void OptionsChanged(ConvertItem item)
    {
        if (!item.IsBusy)
        {
            ResetOutcome(item);
        }

        RaiseChanged();
    }

    /// <summary>Copies this file's options to other files with the same source format and target. Returns how many changed.</summary>
    public int ApplyOptionsToSimilar(ConvertItem source)
    {
        if (source.Importer is null || source.TargetFormatId is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var other in Items.Where(i => i != source && !i.IsBusy && i.Importer?.Format.Id == source.Importer.Format.Id))
        {
            other.ImportOptions = source.ImportOptions.Clone();
            other.TargetFormatId = source.TargetFormatId;
            other.TargetRemembered = false;
            other.SetExportOptions(source.TargetFormatId, source.ExportOptions.Clone());
            ResetOutcome(other);
            count++;
        }

        RaiseChanged();
        return count;
    }

    /// <summary>Validation errors that block converting this file (empty when it is ready).</summary>
    public IReadOnlyList<string> GetBlockingErrors(ConvertItem item)
    {
        var errors = new List<string>();
        if (!item.IsSupported)
        {
            errors.Add("AllInOne PDF cannot read this type of file.");
            return errors;
        }

        if (!item.HasSource)
        {
            errors.Add("The uploaded file is no longer available. Add it again to convert it.");
            return errors;
        }

        var exporter = GetExporter(item);
        if (exporter is null)
        {
            errors.Add("Choose a target format.");
            return errors;
        }

        if (exporter.Status == ConverterStatus.Planned)
        {
            errors.Add($"{exporter.Format.DisplayName} output is coming soon.");
        }

        errors.AddRange(ConvertOptionRules.Errors(item.Importer!.Options, item.ImportOptions).Select(e => $"{e.Definition.Label}: {e.Error}"));
        errors.AddRange(ConvertOptionRules.Errors(exporter.Options, item.ExportOptions).Select(e => $"{e.Definition.Label}: {e.Error}"));
        return errors;
    }

    public bool CanConvert(ConvertItem item) => !item.IsBusy && GetBlockingErrors(item).Count == 0;

    // ---- Output ---------------------------------------------------------------------------------

    public async Task SetOutputDirectoryAsync(string directory)
    {
        OutputDirectory = directory;
        _appState.LastOutputDirectory = directory;
        await _preferences.UpdateAsync(p => p.DefaultOutputDirectory = directory);
        RaiseChanged();
    }

    public async Task SetSaveNextToSourceAsync(bool value)
    {
        await _preferences.UpdateAsync(p => p.SaveNextToSource = value);
        RaiseChanged();
    }

    public async Task SetOpenAfterConvertAsync(bool value)
    {
        await _preferences.UpdateAsync(p => p.OpenAfterConvert = value);
        RaiseChanged();
    }

    public async Task SetRevealAfterConvertAsync(bool value)
    {
        await _preferences.UpdateAsync(p => p.RevealAfterConvert = value);
        RaiseChanged();
    }

    public async Task SetOverwriteAsync(OverwritePolicy policy)
    {
        await _preferences.UpdateAsync(p => p.OverwritePolicy = policy);
        RaiseChanged();
    }

    /// <summary>Folder this file's output will be written to, or null when outputs stay in memory (web).</summary>
    public string? ResolveOutputDirectory(ConvertItem item)
    {
        if (!WritesToDisk)
        {
            return null;
        }

        if (SaveNextToSource && item.SourcePath is not null)
        {
            return Path.GetDirectoryName(item.SourcePath);
        }

        return OutputDirectory ?? _preferences.Current.DefaultOutputDirectory;
    }

    /// <summary>Files whose predicted primary output already exists (desktop only).</summary>
    public IReadOnlyList<ConvertItem> FindConflicts(IEnumerable<ConvertItem> items)
    {
        if (!WritesToDisk)
        {
            return [];
        }

        var conflicts = new List<ConvertItem>();
        foreach (var item in items)
        {
            var dir = ResolveOutputDirectory(item);
            var exporter = GetExporter(item);
            if (dir is null || exporter is null)
            {
                continue;
            }

            if (File.Exists(Path.Combine(dir, OutputBaseName(item) + exporter.Format.PrimaryExtension)))
            {
                conflicts.Add(item);
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Marks a finished web conversion as downloaded: its output bytes are released and an in-memory source moves
    /// to a temporary file, so the workspace no longer holds either in memory. "Convert again" still works.
    /// </summary>
    public async Task MarkDownloadedAsync(ConvertItem item)
    {
        if (item.Outcome is not { } outcome || item.IsBusy)
        {
            return;
        }

        outcome.MarkDownloaded();
        await SpoolContentAsync(item);
        RaiseChanged();
    }

    // ---- Converting -----------------------------------------------------------------------------

    /// <summary>
    /// The theme a job uses: the file's own theme option when the user picked one in the options drawer, otherwise
    /// the store's default theme at the time of converting.
    /// </summary>
    public string? ResolveThemeId(ConvertItem item)
    {
        var exporter = GetExporter(item);
        if (exporter is null || !exporter.SupportsTheme)
        {
            return null;
        }

        var themeKey = ThemeOptionKey(exporter);
        var explicitTheme = themeKey is null ? null : item.ExportOptions.GetString(themeKey);
        return string.IsNullOrWhiteSpace(explicitTheme) ? _defaultThemeId : explicitTheme;
    }

    /// <summary>The store's default theme id (cached; refreshed whenever the theme store changes).</summary>
    public string? DefaultThemeId => _defaultThemeId;

    public ConversionRequest BuildRequest(ConvertItem item, bool overwrite)
    {
        var exporter = GetExporter(item) ?? throw new InvalidOperationException("No target format selected.");
        var exportOptions = item.ExportOptions.Clone();
        var themeId = ResolveThemeId(item);
        var themeKey = ThemeOptionKey(exporter);
        if (themeKey is not null && themeId is not null)
        {
            exportOptions.Set(themeKey, themeId);
        }

        var sourcePath = item.SourcePath ?? (item.Content is null ? item.SpoolPath : null);
        return new ConversionRequest
        {
            SourcePath = sourcePath,
            SourceContent = sourcePath is null ? item.Content : null,
            SourceFileName = item.FileName,
            TargetFormatId = exporter.Format.Id,
            ImportOptions = item.ImportOptions.Clone(),
            ExportOptions = exportOptions,
            ThemeId = themeId,
            OutputDirectory = ResolveOutputDirectory(item),
            OutputBaseName = OutputBaseName(item),
            Overwrite = overwrite,
        };
    }

    /// <summary>
    /// Base name of this file's outputs: the source name, plus its extension when another listed file of the same
    /// name would produce an output with the same name (<see cref="ConvertRules.OutputBaseName"/>).
    /// </summary>
    public string OutputBaseName(ConvertItem item)
    {
        var others = Items.Where(i => i != item).Select(i => (i.FileName, GetExporter(i)?.Format.PrimaryExtension));
        return ConvertRules.OutputBaseName(item.FileName, GetExporter(item)?.Format.PrimaryExtension, others);
    }

    /// <summary>Queues one file. Returns false when it cannot be converted right now.</summary>
    public bool Enqueue(ConvertItem item, bool overwrite)
    {
        StartBatchIfIdle();
        return EnqueueCore(item, overwrite);
    }

    private void StartBatchIfIdle()
    {
        // A new batch starts when nothing is running, so the summary reads "2 of 2 done" rather than counting old runs.
        if (!Items.Any(i => i.IsBusy))
        {
            _batchStart = DateTimeOffset.Now;
            Interlocked.Exchange(ref _batchAnnounced, 0);
        }
    }

    private bool EnqueueCore(ConvertItem item, bool overwrite)
    {
        if (!CanConvert(item))
        {
            return false;
        }

        var request = BuildRequest(item, overwrite);
        var previous = item.Job;

        item.ResetFinishHandled();
        item.ShowDetails = false;
        item.Outcome?.Release();
        item.Outcome = null;
        item.LastRunAt = DateTimeOffset.Now;

        // The queue raises Changed for "queued" before Enqueue returns; that first event is not needed. If the job
        // somehow finished before the assignment, run the finish handling now (it is idempotent).
        var job = _queue.Enqueue(request);
        item.Job = job;
        if (job.IsFinished)
        {
            OnJobChanged(job);
        }

        if (previous is not null)
        {
            _queue.Remove(previous);
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Queues every file that is ready (not converting, valid). Returns how many were queued.</summary>
    public int EnqueueAll(IEnumerable<ConvertItem> items, bool overwrite)
    {
        StartBatchIfIdle();
        var count = 0;
        foreach (var item in items)
        {
            if (EnqueueCore(item, overwrite))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Files the "Convert all" button would convert: ready, failed or cancelled ones (not already done).</summary>
    public IReadOnlyList<ConvertItem> PendingItems() =>
        Items.Where(i => i.State is ConvertItemState.Ready or ConvertItemState.Failed or ConvertItemState.Cancelled && CanConvert(i)).ToList();

    public void Cancel(ConvertItem item) => item.Job?.Cancel();

    public void CancelAll()
    {
        foreach (var item in Items.Where(i => i.IsBusy))
        {
            item.Job?.Cancel();
        }
    }

    /// <summary>
    /// Counts over every listed file that was converted or is converting (rows that are Ready are not counted), so
    /// the status line agrees with the rows: "3 of 5 converted · 1 failed · 1 converting".
    /// </summary>
    public ConvertBatchSummary Summary() => ConvertRules.Summarize(HasRun(Items).Select(i => i.State));

    /// <summary>Counts over the current batch only (the files queued since nothing was converting), for the end-of-batch toast.</summary>
    public ConvertBatchSummary BatchSummary()
    {
        var batchStart = _batchStart;
        return ConvertRules.Summarize(HasRun(Items).Where(i => i.LastRunAt is { } at && at >= batchStart).Select(i => i.State));
    }

    private static IEnumerable<ConvertItem> HasRun(IEnumerable<ConvertItem> items) =>
        items.Where(i => i.Job is not null || i.Outcome is not null);

    /// <summary>Overall progress (0–100) over the same files as <see cref="Summary"/>.</summary>
    public int OverallPercent()
    {
        var run = HasRun(Items).ToList();
        if (run.Count == 0)
        {
            return 0;
        }

        return (int)Math.Round(run.Average(i => i.IsBusy ? i.Job?.Progress.Overall ?? 0 : 1) * 100);
    }

    // ---- Internals ------------------------------------------------------------------------------

    private ConvertItem Create(string fileName, string? path, byte[]? content, string? spoolPath, long size)
    {
        var importer = _registry.FindImporter(fileName);
        var item = new ConvertItem(fileName, path, content, spoolPath, size, importer)
        {
            ExportOptionsFactory = CreateExportOptions,
        };

        if (item.IsSupported)
        {
            var targets = GetTargets(item).Where(t => t.Status != ConverterStatus.Planned).Select(t => t.Format.Id).ToList();
            string? remembered;
            lock (_gate)
            {
                remembered = _lastTargetBySource.GetValueOrDefault(importer!.Format.Id);
            }

            item.TargetFormatId = ConvertRules.DefaultTarget(targets, remembered);

            // The row says so ("Last used for Markdown files"), so a remembered target is never a silent surprise.
            item.TargetRemembered = remembered is not null && string.Equals(item.TargetFormatId, remembered, StringComparison.OrdinalIgnoreCase);
        }

        return item;
    }

    private void AddCore(ConvertItem item)
    {
        lock (_gate)
        {
            _items.Add(item);
        }

        RaiseChanged();
    }

    private static void ResetOutcome(ConvertItem item)
    {
        if (item.IsBusy || item.Outcome is null)
        {
            return;
        }

        item.Outcome.Release();
        item.Outcome = null;
        item.ShowDetails = false;
    }

    /// <summary>
    /// Default export options. The theme option is left unset on purpose: unset means "the default theme when
    /// converting" (<see cref="ResolveThemeId"/>); a value means the user picked a theme for this file.
    /// </summary>
    private OptionValues CreateExportOptions(string targetFormatId)
    {
        var exporter = _registry.FindExporter(targetFormatId);
        if (exporter is null)
        {
            return new OptionValues();
        }

        var values = OptionValues.FromDefaults(exporter.Options);
        if (ThemeOptionKey(exporter) is { } themeKey)
        {
            values.Remove(themeKey);
        }

        return values;
    }

    private static string? ThemeOptionKey(IExporter exporter) =>
        exporter.Options.FirstOrDefault(o => o.Type == OptionType.Theme)?.Key
        ?? (exporter.SupportsTheme && exporter.Options.Any(o => o.Key == PdfExporter.OptTheme) ? PdfExporter.OptTheme : null);

    /// <summary>Re-reads the store's default theme. Convert calls it right before queueing, so jobs use the default at convert time.</summary>
    public async Task RefreshDefaultThemeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _defaultThemeId = await _themes.GetDefaultThemeIdAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the default theme");
        }
    }

    private void OnThemesChanged() => _ = RefreshAndNotifyAsync();

    private async Task RefreshAndNotifyAsync()
    {
        await RefreshDefaultThemeAsync();
        RaiseChanged();
    }

    private ConvertItem? FindItem(ConversionJob job)
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(i => ReferenceEquals(i.Job, job));
        }
    }

    private void OnJobChanged(ConversionJob job)
    {
        if (_disposed)
        {
            return;
        }

        var item = FindItem(job);
        if (item is null)
        {
            return;
        }

        RaiseChanged();

        if (job.IsFinished && ReferenceEquals(item.Job, job) && item.TryMarkFinishHandled())
        {
            _ = HandleFinishedAsync(item, job);
        }
    }

    private async Task HandleFinishedAsync(ConvertItem item, ConversionJob job)
    {
        try
        {
            if (job.Status == JobStatus.Succeeded && job.Result?.PrimaryPath is { } primary)
            {
                _appState.LastOutputDirectory = Path.GetDirectoryName(primary);
            }

            if (_preferences.Current.KeepHistory && job.Status != JobStatus.Cancelled)
            {
                var sourceFormat = item.Importer?.Format.Id ?? "";
                var entry = FileConversionHistory.FromJob(job, sourceFormat);
                if (item.IsUpload)
                {
                    // Spooled uploads run from a temporary file; history must not point at it.
                    entry.SourcePath = null;
                }

                await _history.AddAsync(entry);
            }

            if (job.Status == JobStatus.Succeeded && _host.SupportsShellOpen && job.Result?.PrimaryPath is { } path)
            {
                if (_preferences.Current.OpenAfterConvert)
                {
                    await _host.OpenWithDefaultAppAsync(path);
                }

                // Reveal once per batch, when the last running job finishes.
                if (_preferences.Current.RevealAfterConvert && !_preferences.Current.OpenAfterConvert && Summary().Active == 0)
                {
                    await _host.RevealInFolderAsync(path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-conversion step failed for {File}", item.FileName);
        }

        // Snapshot the result and let the job (with its request's source bytes and result bytes) leave the queue.
        if (ReferenceEquals(item.Job, job))
        {
            item.Outcome = new ConvertOutcome(job, keepDownloads: !WritesToDisk, includeErrorDetails: _host.Kind == HostKind.Desktop, sourceWasEmpty: item.Size == 0);
            item.Job = null;
        }

        ItemFinished?.Invoke(new ConvertItemFinished(item, job));
        _queue.Remove(job);

        // One "batch finished" per batch: with several jobs ending together each finish handler used to see an idle
        // list and the page showed the same "3 files converted" toast three times.
        if (!Items.Any(i => i.IsBusy) && Interlocked.Exchange(ref _batchAnnounced, 1) == 0)
        {
            BatchFinished?.Invoke(new ConvertBatchFinished(BatchSummary(), item, job));
        }

        // Desktop: the output is on disk, so a successful upload's bytes can leave memory now.
        if (job.Status == JobStatus.Succeeded && WritesToDisk)
        {
            await SpoolContentAsync(item);
        }

        RaiseChanged();
    }

    /// <summary>Moves an upload's in-memory bytes to a temporary file (keeps "Convert again" working without the memory).</summary>
    private async Task SpoolContentAsync(ConvertItem item)
    {
        if (item.Content is not { } content || item.IsBusy)
        {
            return;
        }

        var path = NewSpoolPath(item.FileName);
        try
        {
            await File.WriteAllBytesAsync(path, content);
            lock (_gate)
            {
                if (!_items.Contains(item))
                {
                    TryDeleteSpool(path);
                    return;
                }

                item.SpoolPath = path;
                item.Content = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the bytes in memory; nothing is lost.
            _logger.LogWarning(ex, "Could not move {File} to the cache", item.FileName);
            TryDeleteSpool(path);
        }
    }

    private string NewSpoolPath(string fileName)
    {
        var dir = Path.Combine(_spoolDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var safe = string.Join("_", Path.GetFileName(fileName).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return Path.Combine(dir, string.IsNullOrWhiteSpace(safe) ? "upload" : safe);
    }

    private void ReleaseSource(ConvertItem item)
    {
        item.Content = null;
        if (item.SpoolPath is { } spool)
        {
            item.SpoolPath = null;
            TryDeleteSpool(spool);
        }
    }

    private void TryDeleteSpool(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use by a cancelled job; the workspace directory is removed on dispose or by the stale sweep.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not delete {Path} yet", path);
            }
        }
    }

    /// <summary>Removes spool folders left behind by sessions that ended abruptly (older than a day).</summary>
    private void CleanStaleSpoolDirectories()
    {
        try
        {
            var root = Path.Combine(_paths.CacheDirectory, "uploads");
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (FilePaths.AreEqual(dir, _spoolDirectory) || DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < StaleSpoolAge)
                {
                    continue;
                }

                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(ex, "Could not delete stale upload folder {Path}", dir);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Sweeping stale upload folders failed");
        }
    }

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>Tells listeners that something changed that the workspace did not see (prefer <see cref="OptionsChanged"/> for options).</summary>
    public void NotifyChanged() => RaiseChanged();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Changed -= OnJobChanged;
        _themes.Changed -= OnThemesChanged;

        // Free memory held by the singleton queue and stop work nobody can collect any more.
        foreach (var item in Items)
        {
            if (item.Job is { } job)
            {
                _queue.Remove(job);
            }

            item.Content = null;
            item.Outcome?.Release();
        }

        try
        {
            if (Directory.Exists(_spoolDirectory))
            {
                Directory.Delete(_spoolDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not delete the upload folder {Path}", _spoolDirectory);
            }
        }
    }
}
