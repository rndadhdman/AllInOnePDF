using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.Extensions.Logging;

namespace AllInOnePdf.UI.Services;

/// <summary>
/// Editing session for the theme builder (scoped: one per circuit / window). Holds the working copy, the saved
/// snapshot and undo/redo stacks of JSON snapshots. Controls mutate <see cref="Working"/> and then call
/// <see cref="Commit"/>; rapid changes with the same key (slider drags, typing) coalesce into one undo step.
/// </summary>
/// <remarks>
/// Snapshots never carry identity (<c>Id</c>, <c>Name</c>, <c>BasedOn</c>, <c>IsBuiltIn</c>, <c>Modified</c>):
/// undo and redo only change how the theme looks, and restoring a snapshot keeps the identity of the theme that is
/// open now. So after "Save as" (which also clears the history) an undo can never bring back the original theme's
/// id, and a later Save always writes the copy. The name is tracked separately for the "unsaved" indicator.
/// <para>
/// Crash recovery: while the theme is dirty, its full JSON (identity included) is written to the draft store
/// (<c>theme:&lt;id&gt;</c>, <c>theme:new</c>) 1.5 s after the last edit. Saving, Save as, Reset to saved (or undoing
/// back to the saved look) and discarding delete the draft; leaving without saving keeps it.
/// <see cref="CheckForDraftAsync"/> offers a newer, different draft as <see cref="PendingDraft"/>.
/// </para>
/// </remarks>
public sealed class ThemeBuilderState : IDisposable
{
    private const int MaxUndo = 200;
    private const string NewDraftKey = "theme:new";
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan DraftDelay = TimeSpan.FromMilliseconds(1500);

    private readonly IThemeStore _store;
    private readonly IDraftStore? _drafts;
    private readonly DraftAutosaver? _autosaver;
    private readonly ILogger? _logger;
    private string? _draftKey;
    private PdfTheme? _pendingDraft;
    private int _loadGeneration;
    private int _editsSinceLoad;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly Stopwatch _sinceCommit = new();
    private string _savedJson = string.Empty;
    private string _currentJson = string.Empty;
    private string _savedName = string.Empty;
    private string? _lastKey;
    private bool _sourceIsBuiltIn;
    private DateTimeOffset _savedModified;

    /// <summary>Creates the session. Without <paramref name="drafts"/> nothing is autosaved.</summary>
    public ThemeBuilderState(IThemeStore store, IDraftStore? drafts = null, ILogger<ThemeBuilderState>? logger = null)
    {
        _store = store;
        _drafts = drafts;
        _logger = logger;
        _autosaver = drafts is null ? null : new DraftAutosaver(drafts, DraftDelay, logger);
    }

    /// <summary>
    /// When a recovered draft is waiting for the user's decision: the time it was written. Null when there is none.
    /// </summary>
    public DateTimeOffset? PendingDraft { get; private set; }

    /// <summary>Raised after every edit, undo, redo, reset or save.</summary>
    public event Action? Changed;

    /// <summary>The theme being edited. Null until <see cref="Load"/> runs.</summary>
    public PdfTheme? Working { get; private set; }

    /// <summary>Increments on every change; components use it as a cheap "theme changed" parameter.</summary>
    public int Version { get; private set; }

    /// <summary>True when the theme has never been saved (route <c>/themes/new</c>).</summary>
    public bool IsNew { get; private set; }

    /// <summary>Built-in themes cannot be overwritten; saving creates a copy.</summary>
    public bool IsReadOnly => _sourceIsBuiltIn;

    public bool IsDirty => IsNew || HasEdits;

    /// <summary>True when the working copy differs from the loaded or saved state (a new, untouched theme has none).</summary>
    public bool HasEdits => !string.Equals(_currentJson, _savedJson, StringComparison.Ordinal)
        || !string.Equals(Working?.Name, _savedName, StringComparison.Ordinal);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Normalised JSON of the working copy's look (identity cleared) for hashing and change detection.</summary>
    public string CurrentJson => _currentJson;

    public string? LoadedId { get; private set; }

    public void Load(PdfTheme stored, bool isNew)
    {
        _sourceIsBuiltIn = stored.IsBuiltIn;
        _savedModified = stored.Modified;
        Working = stored.Clone();
        Working.IsBuiltIn = stored.IsBuiltIn;
        IsNew = isNew;
        LoadedId = isNew ? "new" : stored.Id;
        _savedJson = Snapshot(Working);
        _currentJson = _savedJson;
        _savedName = Working.Name;
        _draftKey = isNew ? NewDraftKey : DraftKeyFor(stored.Id);
        _pendingDraft = null;
        PendingDraft = null;
        _loadGeneration++;
        _editsSinceLoad = 0;
        ClearHistory();
        Raise();
    }

    /// <summary>The draft key for a saved theme.</summary>
    public static string DraftKeyFor(string themeId) => "theme:" + themeId;

    /// <summary>
    /// Looks for a crash-recovery draft of the theme that was just loaded. A draft written after the last save whose
    /// content differs from the stored theme becomes <see cref="PendingDraft"/>; an outdated or identical one is deleted.
    /// </summary>
    public async Task CheckForDraftAsync(CancellationToken cancellationToken = default)
    {
        if (_drafts is null || _draftKey is not { } key || Working is null)
        {
            return;
        }

        var generation = _loadGeneration;
        var draft = await _drafts.TryReadAsync(key, cancellationToken);
        if (draft is null || generation != _loadGeneration || _editsSinceLoad > 0)
        {
            return;
        }

        PdfTheme? theme = null;
        try
        {
            theme = PdfTheme.FromJson(Encoding.UTF8.GetString(draft.Content));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _logger?.LogWarning(ex, "Ignoring the unreadable theme draft {Key}.", key);
        }

        var outdated = theme is null
            || (!IsNew && draft.Written <= _savedModified)
            || (string.Equals(Snapshot(theme), _savedJson, StringComparison.Ordinal) && string.Equals(theme.Name, _savedName, StringComparison.Ordinal));
        if (outdated)
        {
            await _drafts.DeleteAsync(key, cancellationToken);
            return;
        }

        _pendingDraft = theme;
        PendingDraft = draft.Written;
        Raise();
    }

    /// <summary>Applies the recovered draft as one undo step (the theme becomes dirty and keeps its identity).</summary>
    public void RestoreDraft()
    {
        if (Working is null || _pendingDraft is not { } draft)
        {
            return;
        }

        _pendingDraft = null;
        PendingDraft = null;
        var json = Snapshot(draft);
        if (!string.Equals(json, _currentJson, StringComparison.Ordinal))
        {
            _undo.Push(_currentJson);
            TrimUndo();
            _redo.Clear();
        }

        if (!string.IsNullOrWhiteSpace(draft.Name))
        {
            Working.Name = draft.Name;
        }

        Restore(json);
    }

    /// <summary>Forgets the recovered draft and deletes it (unless edits made since opening have replaced it).</summary>
    public async Task DiscardDraftAsync()
    {
        _pendingDraft = null;
        PendingDraft = null;
        Raise();
        if (_autosaver is not null && _draftKey is { } key && _editsSinceLoad == 0)
        {
            await _autosaver.DiscardAsync(key);
        }
    }

    /// <summary>Deletes drafts older than 30 days in the background (best effort).</summary>
    public void PruneOldDrafts()
    {
        if (_drafts is not null)
        {
            DraftAutosaver.PruneInBackground(_drafts, _logger);
        }
    }

    /// <summary>Flushes a pending draft write.</summary>
    public void Dispose() => _autosaver?.Dispose();

    /// <summary>Records the current working copy as a new undo step (or merges it into the last one when coalescing).</summary>
    public void Commit(string? coalesceKey = null)
    {
        if (Working is null)
        {
            return;
        }

        var json = Snapshot(Working);
        if (string.Equals(json, _currentJson, StringComparison.Ordinal))
        {
            return;
        }

        var coalesce = coalesceKey is not null && coalesceKey == _lastKey && _sinceCommit.IsRunning && _sinceCommit.Elapsed < CoalesceWindow;
        if (!coalesce)
        {
            _undo.Push(_currentJson);
            TrimUndo();
        }

        _redo.Clear();
        _currentJson = json;
        _lastKey = coalesceKey;
        _sinceCommit.Restart();
        Raise(edited: true);
    }

    /// <summary>Renames the working copy. The name is identity, so it is not an undo step.</summary>
    public void Rename(string name)
    {
        if (Working is null || string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), Working.Name, StringComparison.Ordinal))
        {
            return;
        }

        Working.Name = name.Trim();
        Raise(edited: true);
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Push(_currentJson);
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(_currentJson);
        Restore(_redo.Pop());
    }

    /// <summary>Discards edits since the last save (the look is undoable; the name goes back to the saved one).</summary>
    public void ResetToSaved()
    {
        if (Working is null || !IsDirty)
        {
            return;
        }

        if (!string.Equals(_currentJson, _savedJson, StringComparison.Ordinal))
        {
            _undo.Push(_currentJson);
            _redo.Clear();
        }

        Working.Name = _savedName;
        Restore(_savedJson);
    }

    /// <summary>Applies every visual setting of <paramref name="preset"/> while keeping identity (id, name, description, author, tags).</summary>
    public void ApplyPreset(PdfTheme preset)
    {
        if (Working is null)
        {
            return;
        }

        var next = preset.Clone();
        next.Id = Working.Id;
        next.Name = Working.Name;
        next.Description = Working.Description;
        next.Author = Working.Author;
        next.Tags = [.. Working.Tags];
        next.Modified = Working.Modified;
        next.IsBuiltIn = Working.IsBuiltIn;
        next.BasedOn = preset.Id;
        Working = next;
        Commit();
    }

    /// <summary>Saves in place. Built-in themes are read-only: call <see cref="SaveAsCopyAsync"/> instead.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (Working is null)
        {
            return;
        }

        if (IsReadOnly)
        {
            throw new InvalidOperationException("Built-in themes are read-only. Save a copy instead.");
        }

        var toSave = Working.Clone();
        toSave.IsBuiltIn = false;
        await _store.SaveAsync(toSave, cancellationToken);
        Working.Modified = toSave.Modified;
        _savedModified = toSave.Modified;
        IsNew = false;
        LoadedId = Working.Id;
        _savedJson = _currentJson;
        _savedName = Working.Name;
        await SwitchDraftKeyAsync(DraftKeyFor(Working.Id));
        Raise();
    }

    /// <summary>
    /// Saves the working copy under a new id and name, then continues editing the copy. Undo/redo history is cleared:
    /// it belongs to the original theme.
    /// </summary>
    public async Task<PdfTheme> SaveAsCopyAsync(string name, CancellationToken cancellationToken = default)
    {
        if (Working is null)
        {
            throw new InvalidOperationException("No theme is loaded.");
        }

        var copy = Working.Duplicate(string.IsNullOrWhiteSpace(name) ? Working.Name + " copy" : name.Trim());
        if (_sourceIsBuiltIn || IsNew)
        {
            copy.BasedOn = Working.BasedOn ?? Working.Id;
        }

        await _store.SaveAsync(copy, cancellationToken);
        Working = copy.Clone();
        _sourceIsBuiltIn = false;
        IsNew = false;
        LoadedId = copy.Id;
        _savedModified = copy.Modified;
        _currentJson = Snapshot(Working);
        _savedJson = _currentJson;
        _savedName = Working.Name;
        ClearHistory();
        await SwitchDraftKeyAsync(DraftKeyFor(copy.Id));
        Raise();
        return copy;
    }

    /// <summary>Marks the session as abandoned (the user discarded changes and left) so the next visit reloads from the store.</summary>
    public void Forget() => LoadedId = null;

    /// <summary>JSON of how the theme looks: identity and volatile fields cleared, so equality means "looks the same".</summary>
    public static string Snapshot(PdfTheme theme)
    {
        var (id, name, basedOn, modified, builtIn) = (theme.Id, theme.Name, theme.BasedOn, theme.Modified, theme.IsBuiltIn);
        try
        {
            theme.Id = string.Empty;
            theme.Name = string.Empty;
            theme.BasedOn = null;
            theme.Modified = default;
            theme.IsBuiltIn = false;
            return theme.ToJson();
        }
        finally
        {
            theme.Id = id;
            theme.Name = name;
            theme.BasedOn = basedOn;
            theme.Modified = modified;
            theme.IsBuiltIn = builtIn;
        }
    }

    /// <summary>Applies a look snapshot to the theme that is open now, keeping its identity.</summary>
    private void Restore(string json)
    {
        var theme = PdfTheme.FromJson(json);
        if (Working is not null)
        {
            theme.Id = Working.Id;
            theme.Name = Working.Name;
            theme.BasedOn = Working.BasedOn;
        }

        theme.IsBuiltIn = _sourceIsBuiltIn;
        theme.Modified = _savedModified;
        Working = theme;
        _currentJson = json;
        _lastKey = null;
        Raise(edited: true);
    }

    /// <summary>After a save: the old draft is obsolete, and later drafts go under the saved theme's key.</summary>
    private async Task SwitchDraftKeyAsync(string newKey)
    {
        var oldKey = _draftKey;
        _draftKey = newKey;
        _pendingDraft = null;
        PendingDraft = null;
        if (_autosaver is null)
        {
            return;
        }

        if (oldKey is not null)
        {
            await _autosaver.DiscardAsync(oldKey);
        }

        if (!string.Equals(oldKey, newKey, StringComparison.Ordinal))
        {
            await _autosaver.DiscardAsync(newKey);
        }
    }

    /// <summary>Schedules a draft write while the theme is dirty; deletes the draft once it is back to the saved state.</summary>
    private void UpdateDraft()
    {
        if (_autosaver is null || Working is null || _draftKey is not { } key)
        {
            return;
        }

        if (!IsDirty)
        {
            _ = _autosaver.DiscardAsync(key);
            return;
        }

        // Capture immutable values now; the JSON is built off the UI thread.
        var (json, id, name, basedOn, modified, builtIn) = (_currentJson, Working.Id, Working.Name, Working.BasedOn, _savedModified, _sourceIsBuiltIn);
        _autosaver.Schedule(key, () =>
        {
            var theme = PdfTheme.FromJson(json);
            theme.Id = id;
            theme.Name = name;
            theme.BasedOn = basedOn;
            theme.Modified = modified;
            theme.IsBuiltIn = builtIn;
            return Encoding.UTF8.GetBytes(theme.ToJson());
        });
    }

    private void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _lastKey = null;
        _sinceCommit.Reset();
    }

    private void TrimUndo()
    {
        if (_undo.Count <= MaxUndo)
        {
            return;
        }

        var keep = _undo.Take(MaxUndo).Reverse().ToList();
        _undo.Clear();
        foreach (var item in keep)
        {
            _undo.Push(item);
        }
    }

    private void Raise(bool edited = false)
    {
        Version++;
        if (edited)
        {
            _editsSinceLoad++;
            UpdateDraft();
        }

        Changed?.Invoke();
    }
}
