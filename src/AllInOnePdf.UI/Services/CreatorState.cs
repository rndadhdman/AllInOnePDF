using System.Diagnostics;
using System.Text.Json;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Content;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Rendering;
using AllInOnePdf.Publishing.Serialization;
using AllInOnePdf.Publishing.Storage;
using AllInOnePdf.Publishing.Validation;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.Extensions.Logging;

namespace AllInOnePdf.UI.Services;

/// <summary>What the Creator's selection points at.</summary>
public enum CreatorSelectionKind
{
    /// <summary>Nothing selected: the inspector shows the document settings.</summary>
    Document,

    /// <summary>A section.</summary>
    Section,

    /// <summary>A row.</summary>
    Row,

    /// <summary>A column.</summary>
    Column,

    /// <summary>A module.</summary>
    Module,
}

/// <summary>The current selection in the Creator.</summary>
/// <param name="Kind">What is selected.</param>
/// <param name="Id">Id of the selected element; null for the document.</param>
public readonly record struct CreatorSelection(CreatorSelectionKind Kind, string? Id)
{
    /// <summary>Nothing selected.</summary>
    public static CreatorSelection None => new(CreatorSelectionKind.Document, null);
}

/// <summary>Where an element lives in the publication tree.</summary>
/// <param name="Kind">The element kind.</param>
/// <param name="Section">The section (the element itself or its ancestor).</param>
/// <param name="Row">The row, for rows, columns and modules.</param>
/// <param name="Column">The column, for columns and modules.</param>
/// <param name="Module">The module, for modules.</param>
/// <param name="Index">Position of the element within its parent list (for a free-flow item: its index in <see cref="Section.Items"/>).</param>
/// <param name="Item">For a module placed on a free-flow canvas: its placement (Row and Column are then null).</param>
public sealed record CreatorLocation(CreatorSelectionKind Kind, Section Section, Row? Row, Column? Column, Module? Module, int Index, PlacedModule? Item = null);

/// <summary>
/// Editing session for the Creator (scoped: one per circuit / window). Holds the working publication, the saved
/// snapshot, undo/redo stacks of JSON snapshots and the selection. Controls mutate <see cref="Working"/> and call
/// <see cref="Commit"/>; rapid changes with the same key (typing, slider drags) coalesce into one undo step.
/// Structural operations each commit exactly one step.
/// </summary>
/// <remarks>
/// Snapshots exclude identity (<c>Id</c>, <c>Name</c>, <c>Created</c>, <c>Modified</c>) like
/// <see cref="ThemeBuilderState.Snapshot"/>. Asset bytes are not part of the JSON, so the state keeps every asset's
/// bytes by id and re-attaches them when a snapshot is restored (an undo can bring back a removed image).
/// <para>
/// Crash recovery: while the publication is dirty, it is written as an <c>.aiopub</c> package (images included) to the
/// draft store under <c>publication:&lt;route id&gt;</c> 2 s after the last edit; a write that falls due while the
/// previous one runs follows right after it. Saving, Save as, Reset to saved and discarding delete the draft; leaving
/// without saving keeps it. <see cref="CheckForDraftAsync"/> offers a newer, different draft as <see cref="PendingDraft"/>.
/// </para>
/// </remarks>
public sealed class CreatorState : IDisposable
{
    /// <summary>Most undo steps kept.</summary>
    public const int MaxUndo = 200;

    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan DraftDelay = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = PublishingJson.Options;

    private readonly IPublicationStore _store;
    private readonly CreatorThumbnailCache _thumbnails;
    private readonly IDraftStore? _drafts;
    private readonly DraftAutosaver? _autosaver;
    private readonly ILogger? _logger;
    private string? _draftKey;
    private string? _restoreOnOpen;
    private Publication? _pendingDraft;
    private DateTimeOffset _savedModified;
    private int _loadGeneration;
    private int _editsSinceLoad;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly Stopwatch _sinceCommit = new();
    private readonly Dictionary<string, byte[]> _assetData = new(StringComparer.Ordinal);
    private string _savedJson = string.Empty;
    private string _currentJson = string.Empty;
    private string _savedName = string.Empty;
    private string? _lastKey;

    /// <summary>Creates the session. Without <paramref name="drafts"/> nothing is autosaved.</summary>
    public CreatorState(IPublicationStore store, CreatorThumbnailCache thumbnails, IDraftStore? drafts = null, ILogger<CreatorState>? logger = null)
    {
        _store = store;
        _thumbnails = thumbnails;
        _drafts = drafts;
        _logger = logger;
        _autosaver = drafts is null ? null : new DraftAutosaver(drafts, DraftDelay, logger);
    }

    /// <summary>
    /// When a recovered draft is waiting for the user's decision: the time it was written. Null when there is none.
    /// </summary>
    public DateTimeOffset? PendingDraft { get; private set; }

    /// <summary>Raised after every edit, undo, redo, selection change, reset or save, and when a new <see cref="PageMap"/> lands (from a thread-pool thread: marshal to the renderer).</summary>
    public event Action? Changed;

    /// <summary>The publication being edited. Null until <see cref="Load"/> runs.</summary>
    public Publication? Working { get; private set; }

    /// <summary>
    /// Where the PDF puts every section, row, column and module, from the latest background render of the working
    /// copy (about half a second after an edit settles). Null until the first render lands or when no renderer is
    /// registered. <see cref="CreatorPageMap.Json"/> tells which content it describes; a stale map is still the best
    /// guess for the canvas until the next one arrives.
    /// </summary>
    public CreatorPageMap? PageMap { get; private set; }

    /// <summary>Increments on every change; components use it as a cheap "something changed" parameter.</summary>
    public int Version { get; private set; }

    /// <summary>Increments only when the content or look changes (not on selection changes).</summary>
    public int ContentVersion { get; private set; }

    /// <summary>True when the publication has never been saved (route <c>/create/new:&lt;template&gt;</c>).</summary>
    public bool IsNew { get; private set; }

    /// <summary>True when there are changes since the last save (or the publication was never saved).</summary>
    public bool IsDirty => IsNew || HasEdits;

    /// <summary>True when the working copy differs from the loaded or saved state (a fresh, untouched template has none).</summary>
    public bool HasEdits => !string.Equals(_currentJson, _savedJson, StringComparison.Ordinal)
        || !string.Equals(Working?.Name, _savedName, StringComparison.Ordinal);

    /// <summary>True when an undo step exists.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>True when a redo step exists.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>JSON snapshot of the working copy (identity cleared), for hashing and change detection.</summary>
    public string CurrentJson => _currentJson;

    /// <summary>The route id the session was loaded for (<c>new:&lt;template&gt;</c> or a publication id).</summary>
    public string? LoadedId { get; private set; }

    /// <summary>The current selection.</summary>
    public CreatorSelection Selection { get; private set; } = CreatorSelection.None;

    /// <summary>Last structural change, phrased for a screen reader live region.</summary>
    public string? Announcement { get; private set; }

    // ------------------------------------------------------------------ session

    /// <summary>Starts editing a copy of <paramref name="publication"/>. History and selection are cleared.</summary>
    /// <param name="publication">The stored publication or a fresh one from a template.</param>
    /// <param name="isNew">True when it has never been saved.</param>
    /// <param name="routeId">The route id to remember (defaults to the publication id).</param>
    public void Load(Publication publication, bool isNew, string? routeId = null)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var copy = PublicationValidator.Normalize(publication.Clone());
        _assetData.Clear();
        RememberAssets(copy);
        Working = copy;
        IsNew = isNew;
        LoadedId = routeId ?? copy.Id;
        _savedJson = Snapshot(copy);
        _currentJson = _savedJson;
        _savedName = copy.Name;
        Selection = CreatorSelection.None;
        Announcement = null;
        _savedModified = copy.Modified;
        _draftKey = DraftKeyFor(LoadedId);
        _pendingDraft = null;
        PendingDraft = null;
        _loadGeneration++;
        _editsSinceLoad = 0;
        ClearHistory();
        ContentVersion++;
        PageMap = null;
        Raise();
        SchedulePageMap();
    }

    /// <summary>Delay after the last edit before the working copy is rendered for <see cref="PageMap"/>.</summary>
    private static readonly TimeSpan PageMapDelay = TimeSpan.FromMilliseconds(450);

    private CancellationTokenSource? _pageMapCts;

    /// <summary>
    /// Renders the working copy on the thread pool (cancelling a pending render) and publishes the result as
    /// <see cref="PageMap"/>, raising <see cref="Changed"/> so the canvas and the tree redraw with the real pages.
    /// </summary>
    private void SchedulePageMap()
    {
        _pageMapCts?.Cancel();
        _pageMapCts?.Dispose();
        _pageMapCts = null;
        if (Working is null || !_thumbnails.RendererAvailable)
        {
            return;
        }

        var cts = _pageMapCts = new CancellationTokenSource();
        var ct = cts.Token;
        var json = _currentJson;
        var snapshot = Working.Clone();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PageMapDelay, ct);
                // No placeholders: the map must describe the pages Export PDF makes, where an empty image module takes no space.
                var result = await _thumbnails.RenderWithPagesAsync(snapshot, new PublicationRenderOptions { Draft = true, ShowPlaceholders = false, EmbedMetadata = false }, ct);
                ct.ThrowIfCancellationRequested();
                PageMap = CreatorPageMap.From(json, result);
                Raise();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // The canvas keeps the previous map; the preview reports render errors itself.
                _logger?.LogDebug(ex, "The page map render failed.");
            }
        }, ct);
    }

    /// <summary>The draft key for a route id (a publication id or <c>new:&lt;template&gt;</c>).</summary>
    public static string DraftKeyFor(string routeId) => CreatorDrafts.KeyPrefix + routeId;

    /// <summary>
    /// Reads the publication out of a draft's content (an <c>.aiopub</c> package, images included) and normalises it.
    /// A damaged or foreign draft throws one of the exceptions <see cref="IsUnreadableDraft"/> recognises.
    /// </summary>
    public static Publication ReadDraft(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var stream = new MemoryStream(content, writable: false);
        return PublicationValidator.Normalize(PublicationPackage.Read(stream).Publication);
    }

    /// <summary>True for the exceptions <see cref="ReadDraft"/> throws for a damaged or foreign draft.</summary>
    public static bool IsUnreadableDraft(Exception ex) =>
        ex is InvalidDataException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException;

    /// <summary>
    /// Asks the next <see cref="CheckForDraftAsync"/> for <paramref name="routeId"/> to restore the draft straight away
    /// instead of offering it (the gallery's Continue). A session already loaded for that route is forgotten so the
    /// builder loads it again and finds the draft.
    /// </summary>
    public void RequestDraftRestore(string routeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(routeId);
        _restoreOnOpen = routeId;
        if (string.Equals(LoadedId, routeId, StringComparison.Ordinal))
        {
            Forget();
        }
    }

    /// <summary>The route whose draft the next <see cref="CheckForDraftAsync"/> restores without asking, if any.</summary>
    public string? DraftRestoreRequest => _restoreOnOpen;

    /// <summary>
    /// Looks for a crash-recovery draft of the publication that was just loaded. A draft written after the last save
    /// whose content differs from the stored publication becomes <see cref="PendingDraft"/>; an outdated or identical
    /// one is deleted. The package is read off the UI thread. When <see cref="RequestDraftRestore"/> asked for this
    /// route, the draft is restored right away instead (the request is used up either way).
    /// </summary>
    public async Task CheckForDraftAsync(CancellationToken cancellationToken = default)
    {
        var autoRestore = _restoreOnOpen is not null && string.Equals(_restoreOnOpen, LoadedId, StringComparison.Ordinal);
        _restoreOnOpen = null;
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

        Publication? publication = null;
        string? snapshot = null;
        try
        {
            (publication, snapshot) = await Task.Run(() =>
            {
                var read = ReadDraft(draft.Content);
                return (read, Snapshot(read));
            }, cancellationToken);
        }
        catch (Exception ex) when (IsUnreadableDraft(ex))
        {
            _logger?.LogWarning(ex, "Ignoring the unreadable publication draft {Key}.", key);
        }

        if (generation != _loadGeneration || _editsSinceLoad > 0)
        {
            return;
        }

        var outdated = publication is null
            || (!IsNew && draft.Written <= _savedModified)
            || (string.Equals(snapshot, _savedJson, StringComparison.Ordinal) && string.Equals(publication.Name, _savedName, StringComparison.Ordinal));
        if (outdated)
        {
            await _drafts.DeleteAsync(key, cancellationToken);
            return;
        }

        _pendingDraft = publication;
        PendingDraft = draft.Written;
        if (autoRestore)
        {
            RestoreDraft();
            return;
        }

        Raise();
    }

    /// <summary>Applies the recovered draft as one undo step (the publication becomes dirty and keeps its identity).</summary>
    public void RestoreDraft()
    {
        if (Working is null || _pendingDraft is not { } draft)
        {
            return;
        }

        _pendingDraft = null;
        PendingDraft = null;
        RememberAssets(draft);
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
        Announce("The recovered changes were restored. Press Ctrl+Z to undo.");
    }

    /// <summary>Forgets the recovered draft and deletes it (unless edits made since opening have replaced it).</summary>
    public async Task DiscardDraftAsync()
    {
        _pendingDraft = null;
        PendingDraft = null;
        Announce("The recovered changes were discarded.");
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

    /// <summary>Flushes a pending draft write and stops a pending page-map render.</summary>
    public void Dispose()
    {
        _pageMapCts?.Cancel();
        _pageMapCts?.Dispose();
        _pageMapCts = null;
        _autosaver?.Dispose();
    }

    /// <summary>Records the working copy as a new undo step (or merges it into the last one when coalescing).</summary>
    /// <param name="coalesceKey">Changes with the same key within 1.2 s become one step; null never coalesces.</param>
    public void Commit(string? coalesceKey = null)
    {
        if (Working is null)
        {
            return;
        }

        RememberAssets(Working);
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
        ContentVersion++;
        Raise(edited: true);
    }

    /// <summary>Renames the publication. The name is identity, so it is not an undo step.</summary>
    public void Rename(string name)
    {
        if (Working is null || string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), Working.Name, StringComparison.Ordinal))
        {
            return;
        }

        Working.Name = name.Trim();
        Raise(edited: true);
    }

    /// <summary>Goes back one step.</summary>
    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Push(_currentJson);
        Restore(_undo.Pop());
        Announce("Undone.");
    }

    /// <summary>Goes forward one step.</summary>
    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(_currentJson);
        Restore(_redo.Pop());
        Announce("Redone.");
    }

    /// <summary>Discards edits since the last save (undoable; the name goes back to the saved one).</summary>
    public void ResetToSaved()
    {
        if (Working is null || !IsDirty || IsNew)
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
        Announce("Changes since the last save were discarded.");
    }

    /// <summary>Saves in place, with a page-1 thumbnail when the renderer is available.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (Working is null)
        {
            return;
        }

        var toSave = PrepareForSave(Working);
        var thumbnail = await _thumbnails.TryRenderThumbnailPngAsync(toSave, cancellationToken);
        await _store.SaveAsync(toSave, thumbnail, cancellationToken);
        Working.Modified = toSave.Modified;
        _savedModified = toSave.Modified;
        IsNew = false;
        LoadedId = Working.Id;
        _savedJson = _currentJson;
        _savedName = Working.Name;
        _thumbnails.InvalidatePublication(Working.Id);
        await SwitchDraftKeyAsync(DraftKeyFor(Working.Id));
        Raise();
    }

    /// <summary>Saves the working copy under a new id and name and continues editing the copy (history is cleared).</summary>
    public async Task<Publication> SaveAsCopyAsync(string name, CancellationToken cancellationToken = default)
    {
        if (Working is null)
        {
            throw new InvalidOperationException("No publication is open.");
        }

        var copy = PrepareForSave(Working);
        copy.Id = Publication.NewId();
        copy.Name = string.IsNullOrWhiteSpace(name) ? Working.Name + " copy" : name.Trim();
        copy.Created = DateTimeOffset.UtcNow;
        var thumbnail = await _thumbnails.TryRenderThumbnailPngAsync(copy, cancellationToken);
        await _store.SaveAsync(copy, thumbnail, cancellationToken);
        await SwitchDraftKeyAsync(DraftKeyFor(copy.Id));
        Load(copy, isNew: false);
        return copy;
    }

    /// <summary>After a save: the old draft is obsolete, and later drafts go under the saved publication's key.</summary>
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

    /// <summary>Schedules a draft package while the publication is dirty; deletes the draft once it is back to the saved state.</summary>
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

        // Capture immutable values now (the JSON snapshot, identity, asset bytes by id); the package is built off the UI thread.
        var (json, id, name, created, modified) = (_currentJson, Working.Id, Working.Name, Working.Created, Working.Modified);
        var assets = new Dictionary<string, byte[]>(_assetData, StringComparer.Ordinal);
        _autosaver.Schedule(key, () =>
        {
            var publication = Publication.FromJson(json);
            publication.Id = id;
            publication.Name = name;
            publication.Created = created;
            publication.Modified = modified;
            foreach (var asset in publication.Assets)
            {
                if (assets.TryGetValue(asset.Id, out var data))
                {
                    asset.Data = data;
                }
            }

            return PublicationPackage.Write(publication, thumbnail: null);
        });
    }

    /// <summary>Marks the session as abandoned (the user left without saving) so the next visit reloads from the store.</summary>
    public void Forget() => LoadedId = null;

    // ---------------------------------------------------------------- selection

    /// <summary>Selects an element (or the document when <paramref name="id"/> is null or unknown).</summary>
    public void Select(CreatorSelectionKind kind, string? id)
    {
        var next = id is null || Locate(id) is null ? CreatorSelection.None : new CreatorSelection(kind, id);
        if (next == Selection)
        {
            return;
        }

        Selection = next;
        Raise();
    }

    /// <summary>Selects the element with that id, whatever its kind.</summary>
    public void SelectId(string? id)
    {
        if (id is not null && Locate(id) is { } location)
        {
            Select(location.Kind, id);
        }
        else
        {
            ClearSelection();
        }
    }

    /// <summary>Selects the document.</summary>
    public void ClearSelection() => Select(CreatorSelectionKind.Document, null);

    /// <summary>Finds an element anywhere in the publication.</summary>
    public CreatorLocation? Locate(string? id)
    {
        if (Working is null || string.IsNullOrEmpty(id))
        {
            return null;
        }

        for (var s = 0; s < Working.Sections.Count; s++)
        {
            var section = Working.Sections[s];
            if (section.Id == id)
            {
                return new(CreatorSelectionKind.Section, section, null, null, null, s);
            }

            for (var r = 0; r < section.Rows.Count; r++)
            {
                var row = section.Rows[r];
                if (row.Id == id)
                {
                    return new(CreatorSelectionKind.Row, section, row, null, null, r);
                }

                for (var c = 0; c < row.Columns.Count; c++)
                {
                    var column = row.Columns[c];
                    if (column.Id == id)
                    {
                        return new(CreatorSelectionKind.Column, section, row, column, null, c);
                    }

                    for (var m = 0; m < column.Modules.Count; m++)
                    {
                        if (column.Modules[m].Id == id)
                        {
                            return new(CreatorSelectionKind.Module, section, row, column, column.Modules[m], m);
                        }
                    }
                }
            }

            for (var i = 0; i < section.Items.Count; i++)
            {
                var item = section.Items[i];
                if (item.Module?.Id == id)
                {
                    return new(CreatorSelectionKind.Module, section, null, null, item.Module, i, item);
                }
            }
        }

        return null;
    }

    /// <summary>The placement of the free-flow item whose module has that id, or null.</summary>
    public PlacedModule? FindItem(string? id) => Locate(id)?.Item;

    /// <summary>The module with that id, or null.</summary>
    public Module? FindModule(string? id) => Locate(id)?.Module;

    // --------------------------------------------------------------- structure

    /// <summary>
    /// Adds a section in the publication's <see cref="Publication.DefaultLayout"/> (Structure: with one full-width row;
    /// Free flow: an empty full-page canvas) at <paramref name="index"/> (end when null) and selects it.
    /// </summary>
    public Section? AddSection(int? index = null)
    {
        if (Working is null)
        {
            return null;
        }

        var section = NewSection();
        var at = Math.Clamp(index ?? Working.Sections.Count, 0, Working.Sections.Count);
        Working.Sections.Insert(at, section);
        Selection = new(CreatorSelectionKind.Section, section.Id);
        Announce($"Section {at + 1} of {Working.Sections.Count} added.");
        Commit();
        return section;
    }

    /// <summary>Adds a section (in the default layout) that starts on a new page at the end of the publication and selects it.</summary>
    public Section? AddPage()
    {
        if (Working is null)
        {
            return null;
        }

        var section = NewSection();
        section.PageBreakBefore = Working.Sections.Count > 0;
        Working.Sections.Add(section);
        Selection = new(CreatorSelectionKind.Section, section.Id);
        Announce($"Page added: section {Working.Sections.Count} starts on a new page.");
        Commit();
        return section;
    }

    /// <summary>Adds a row with the given column split to a section and selects it.</summary>
    public Row? AddRow(string sectionId, IReadOnlyList<double>? fractions = null, int? index = null)
    {
        if (Locate(sectionId) is not { Kind: CreatorSelectionKind.Section, Section: var section } || section.Layout == SectionLayout.FreeFlow)
        {
            return null;
        }

        var row = new Row();
        ColumnLayouts.Apply(row, fractions is { Count: > 0 } ? fractions : [1]);
        var at = Math.Clamp(index ?? section.Rows.Count, 0, section.Rows.Count);
        section.Rows.Insert(at, row);
        Selection = new(CreatorSelectionKind.Row, row.Id);
        Announce($"Row {at + 1} of {section.Rows.Count} added with {Columns(row.Columns.Count)}.");
        Commit();
        return row;
    }

    /// <summary>Changes a row's column split; content of removed columns moves to the last remaining column.</summary>
    public void SetRowLayout(string rowId, IReadOnlyList<double> fractions)
    {
        if (Locate(rowId) is not { Kind: CreatorSelectionKind.Row, Row: { } row } || fractions.Count == 0)
        {
            return;
        }

        ColumnLayouts.Apply(row, fractions);
        Announce($"Row layout changed to {Columns(row.Columns.Count)}.");
        Commit();
    }

    /// <summary>Inserts a module into a column at <paramref name="index"/> (end when null) and selects it.</summary>
    public void AddModule(Module module, string columnId, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (Locate(columnId) is not { Kind: CreatorSelectionKind.Column, Column: { } column })
        {
            return;
        }

        var at = Math.Clamp(index ?? column.Modules.Count, 0, column.Modules.Count);
        column.Modules.Insert(at, module);
        Selection = new(CreatorSelectionKind.Module, module.Id);
        Announce($"{CreatorModuleCatalog.NameOf(module)} module {at + 1} of {column.Modules.Count} added.");
        Commit();
    }

    /// <summary>
    /// Inserts a module next to the selection: after a selected module, at the end of a selected column, in the first
    /// column of a selected row, in the last row of a selected section, else at the end of the publication (a section
    /// is created when there is none).
    /// </summary>
    public void InsertModule(Module module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (Working is null)
        {
            return;
        }

        var location = Locate(Selection.Id);
        if (location?.Section is { Layout: SectionLayout.FreeFlow } canvas)
        {
            if (CreatorFreeFlow.Allowed(module))
            {
                AddItemCascaded(canvas, module);
            }

            return;
        }

        if (location is null && Working.Sections.LastOrDefault() is { Layout: SectionLayout.FreeFlow } lastCanvas)
        {
            if (CreatorFreeFlow.Allowed(module))
            {
                AddItemCascaded(lastCanvas, module);
            }

            return;
        }

        if (location is null && Working.Sections.Count == 0 && Working.DefaultLayout == SectionLayout.FreeFlow && CreatorFreeFlow.Allowed(module))
        {
            var created = NewSection();
            Working.Sections.Add(created);
            PlaceNewSelected(created, module, 24, 24);
            Announce($"{CreatorModuleCatalog.NameOf(module)} module added.");
            Commit();
            return;
        }

        switch (location)
        {
            case { Kind: CreatorSelectionKind.Module, Column: { } column }:
                AddModule(module, column.Id, location.Index + 1);
                return;
            case { Kind: CreatorSelectionKind.Column, Column: { } column }:
                AddModule(module, column.Id);
                return;
            case { Kind: CreatorSelectionKind.Row, Row: { } row } when row.Columns.Count > 0:
                AddModule(module, row.Columns[0].Id);
                return;
            case { Kind: CreatorSelectionKind.Section, Section: var section } when section.Rows.Count > 0 && section.Rows[^1].Columns.Count > 0:
                AddModule(module, section.Rows[^1].Columns[0].Id);
                return;
        }

        var last = Working.Sections.LastOrDefault(s => s.Layout == SectionLayout.Structure);
        if (last is null || last.Rows.Count == 0 || last.Rows[^1].Columns.Count == 0)
        {
            // One step: create the container and the module together.
            last ??= new Section();
            if (!Working.Sections.Contains(last))
            {
                Working.Sections.Add(last);
            }

            var row = new Row();
            last.Rows.Add(row);
            row.Columns[0].Modules.Add(module);
            Selection = new(CreatorSelectionKind.Module, module.Id);
            Announce($"{CreatorModuleCatalog.NameOf(module)} module added.");
            Commit();
            return;
        }

        AddModule(module, last.Rows[^1].Columns[^1].Id);
    }

    /// <summary>
    /// Moves an element: modules into a column, rows into a section, sections within the document (target null).
    /// <paramref name="index"/> is the drop position in the target list as it looks before the element is taken out.
    /// </summary>
    /// <returns>True when something moved.</returns>
    public bool Move(string id, string? targetId, int index)
    {
        if (Working is null || Locate(id) is not { } source)
        {
            return false;
        }

        var target = Locate(targetId);
        switch (source.Kind)
        {
            case CreatorSelectionKind.Module when source.Item is { } item:
                if (target is { Kind: CreatorSelectionKind.Column, Column: { } intoColumn })
                {
                    // Off the canvas into a column: the module leaves its box behind.
                    source.Section.Items.RemoveAt(source.Index);
                    intoColumn.Modules.Insert(Math.Clamp(index, 0, intoColumn.Modules.Count), item.Module);
                    Selection = new(CreatorSelectionKind.Module, item.Module.Id);
                    Announce($"{CreatorModuleCatalog.NameOf(item.Module)} module moved into a column.");
                    Commit();
                    return true;
                }

                return target is { Kind: CreatorSelectionKind.Section, Section: { Layout: SectionLayout.FreeFlow } toCanvas } && ReorderItem(id, toCanvas.Id, index);

            case CreatorSelectionKind.Module when target is { Kind: CreatorSelectionKind.Column, Column: { } column }:
                return MoveInList(source.Column!.Modules, source.Index, column.Modules, index, source.Module!, "module");

            case CreatorSelectionKind.Module when target is { Kind: CreatorSelectionKind.Section, Section: { Layout: SectionLayout.FreeFlow } canvas }:
            {
                // From a column onto a canvas: a box of the type's default size, stacked like a click insert.
                var module = source.Module!;
                if (!CreatorFreeFlow.Allowed(module))
                {
                    return false;
                }

                source.Column!.Modules.RemoveAt(source.Index);
                var placed = PlaceNewSelected(canvas, module, CascadeX(canvas), CascadeY(canvas));
                SetFrontIndex(canvas, placed, index);
                Announce($"{CreatorModuleCatalog.NameOf(module)} module moved onto the free-flow canvas.");
                Commit();
                return true;
            }

            case CreatorSelectionKind.Row when target is { Kind: CreatorSelectionKind.Section, Section: { Layout: SectionLayout.Structure } section }:
                return MoveInList(source.Section.Rows, source.Index, section.Rows, index, source.Row!, "row");
            case CreatorSelectionKind.Section:
                return MoveInList(Working.Sections, source.Index, Working.Sections, index, source.Section, "section");
            default:
                return false;
        }
    }

    /// <summary>Moves the element one place up (<paramref name="delta"/> −1) or down (+1) within its parent.</summary>
    public bool MoveBy(string id, int delta)
    {
        if (Working is null || Locate(id) is not { } source || delta == 0)
        {
            return false;
        }

        if (source.Item is not null)
        {
            // Items are listed front to back: up brings the item forward, down sends it backward.
            return delta < 0 ? BringForward(id) : SendBackward(id);
        }

        var to = source.Index + (delta > 0 ? 2 : -1);
        return source.Kind switch
        {
            CreatorSelectionKind.Module => to >= 0 && to <= source.Column!.Modules.Count && Move(id, source.Column.Id, to),
            CreatorSelectionKind.Row => to >= 0 && to <= source.Section.Rows.Count && Move(id, source.Section.Id, to),
            CreatorSelectionKind.Section => to >= 0 && to <= Working.Sections.Count && Move(id, null, to),
            _ => false,
        };
    }

    /// <summary>Duplicates a module, row or section (with new ids) right after the original and selects the copy.</summary>
    public void Duplicate(string id)
    {
        if (Working is null || Locate(id) is not { } source)
        {
            return;
        }

        switch (source.Kind)
        {
            case CreatorSelectionKind.Module when source.Item is { } item:
            {
                var copy = new PlacedModule
                {
                    Module = DeepCopy(item.Module),
                    XPt = ClampPosition(item.XPt + CreatorFreeFlow.DuplicateOffsetPt),
                    YPt = ClampPosition(item.YPt + CreatorFreeFlow.DuplicateOffsetPt),
                    WidthPt = item.WidthPt,
                    HeightPt = item.HeightPt,
                    Z = item.Z,
                    LockAspect = item.LockAspect,
                };

                // Right after the original in the list, same level: it paints just above it.
                source.Section.Items.Insert(source.Index + 1, copy);
                Selection = new(CreatorSelectionKind.Module, copy.Module.Id);
                Announce($"{CreatorModuleCatalog.NameOf(copy.Module)} module duplicated.");
                break;
            }

            case CreatorSelectionKind.Module:
            {
                var copy = DeepCopy(source.Module!);
                source.Column!.Modules.Insert(source.Index + 1, copy);
                Selection = new(CreatorSelectionKind.Module, copy.Id);
                Announce($"{CreatorModuleCatalog.NameOf(copy)} module duplicated.");
                break;
            }

            case CreatorSelectionKind.Row:
            {
                var copy = DeepCopy(source.Row!);
                source.Section.Rows.Insert(source.Index + 1, copy);
                Selection = new(CreatorSelectionKind.Row, copy.Id);
                Announce("Row duplicated.");
                break;
            }

            case CreatorSelectionKind.Section:
            {
                var copy = DeepCopy(source.Section);
                Working.Sections.Insert(source.Index + 1, copy);
                Selection = new(CreatorSelectionKind.Section, copy.Id);
                Announce("Section duplicated.");
                break;
            }

            default:
                return;
        }

        Commit();
    }

    /// <summary>Deletes a module, row or section (columns are removed by changing the row layout) and selects its parent.</summary>
    public void Delete(string id)
    {
        if (Working is null || Locate(id) is not { } source)
        {
            return;
        }

        switch (source.Kind)
        {
            case CreatorSelectionKind.Module when source.Item is not null:
                source.Section.Items.RemoveAt(source.Index);
                Selection = new(CreatorSelectionKind.Section, source.Section.Id);
                Announce($"{CreatorModuleCatalog.NameOf(source.Module!)} module deleted. Press Ctrl+Z to undo.");
                break;
            case CreatorSelectionKind.Module:
                source.Column!.Modules.RemoveAt(source.Index);
                Selection = new(CreatorSelectionKind.Column, source.Column.Id);
                Announce($"{CreatorModuleCatalog.NameOf(source.Module!)} module deleted. Press Ctrl+Z to undo.");
                break;
            case CreatorSelectionKind.Row:
                source.Section.Rows.RemoveAt(source.Index);
                Selection = new(CreatorSelectionKind.Section, source.Section.Id);
                Announce("Row deleted. Press Ctrl+Z to undo.");
                break;
            case CreatorSelectionKind.Section:
                Working.Sections.RemoveAt(source.Index);
                Selection = CreatorSelection.None;
                Announce("Section deleted. Press Ctrl+Z to undo.");
                break;
            case CreatorSelectionKind.Column when source.Row is { Columns.Count: > 1 } row:
            {
                // Removing a column folds the split: the remaining columns share the freed width.
                var fractions = row.Fractions.Where((_, i) => i != source.Index).ToList();
                var modules = source.Column!.Modules;
                row.Columns.RemoveAt(source.Index);
                row.Fractions = fractions;
                ColumnLayouts.Apply(row, fractions);
                row.Columns[Math.Min(source.Index, row.Columns.Count - 1)].Modules.AddRange(modules);
                Selection = new(CreatorSelectionKind.Row, row.Id);
                Announce($"Column deleted; its modules moved to a neighbouring column. Press Ctrl+Z to undo.");
                break;
            }

            default:
                return;
        }

        Commit();
    }

    // --------------------------------------------------------------- free flow

    /// <summary>
    /// True when a click on an Insert tile would put the module on a free-flow canvas (the selection is in a free-flow
    /// section, or nothing is selected and the last section is free flow), where a page break cannot go.
    /// </summary>
    public bool InsertTargetIsFreeFlow
    {
        get
        {
            if (Working is null)
            {
                return false;
            }

            var location = Locate(Selection.Id);
            return location is not null
                ? location.Section.Layout == SectionLayout.FreeFlow
                : Working.Sections.Count > 0 ? Working.Sections[^1].Layout == SectionLayout.FreeFlow : Working.DefaultLayout == SectionLayout.FreeFlow;
        }
    }

    /// <summary>
    /// Sets <see cref="Publication.DefaultLayout"/> (what new sections get) and, when <paramref name="convertExisting"/>,
    /// converts every section to that layout too (<see cref="SetSectionLayout"/>). One undo step.
    /// </summary>
    public void SetDefaultLayout(SectionLayout layout, bool convertExisting)
    {
        if (Working is null)
        {
            return;
        }

        Working.DefaultLayout = layout;
        var converted = 0;
        if (convertExisting)
        {
            foreach (var section in Working.Sections.Where(s => s.Layout != layout))
            {
                ConvertSection(section, layout);
                converted++;
            }
        }

        KeepSelectionValid();
        var name = layout == SectionLayout.FreeFlow ? "Free flow" : "Structure";
        Announce(converted > 0 ? $"{name} for the whole publication: {converted} {(converted == 1 ? "section" : "sections")} converted." : $"New sections use {name}.");
        Commit();
    }

    /// <summary>
    /// Switches a section between Structure and Free flow, keeping its modules. To Free flow, every module is placed where
    /// the latest render painted it (<see cref="CreatorPageMap.Boxes"/>, made relative to the canvas's top-left) and
    /// stacked when the render reported no box for it; back to Structure, items that share a horizontal band become a
    /// row. One undo step.
    /// </summary>
    /// <returns>True when the section changed.</returns>
    public bool SetSectionLayout(string sectionId, SectionLayout layout)
    {
        if (Locate(sectionId) is not { Kind: CreatorSelectionKind.Section, Section: var section } || section.Layout == layout)
        {
            return false;
        }

        ConvertSection(section, layout);
        KeepSelectionValid();
        Announce(layout == SectionLayout.FreeFlow
            ? $"The section is now Free flow with {section.Items.Count} {(section.Items.Count == 1 ? "module" : "modules")} placed by hand."
            : $"The section is now Structure with {section.Rows.Count} {(section.Rows.Count == 1 ? "row" : "rows")}.");
        Commit();
        return true;
    }

    /// <summary>
    /// Places a module on a free-flow section's canvas at (<paramref name="xPt"/>, <paramref name="yPt"/>) with that size,
    /// in front of the other items, and selects it. Images keep their aspect when resized. A page break cannot go on a canvas.
    /// </summary>
    public PlacedModule? AddItem(string sectionId, Module module, double xPt, double yPt, double widthPt, double heightPt)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (Locate(sectionId) is not { Kind: CreatorSelectionKind.Section, Section: { Layout: SectionLayout.FreeFlow } section } || !CreatorFreeFlow.Allowed(module))
        {
            return null;
        }

        var item = PlaceNew(section, module, xPt, yPt, widthPt, heightPt);
        Selection = new(CreatorSelectionKind.Module, module.Id);
        Announce($"{CreatorModuleCatalog.NameOf(module)} module placed on the canvas.");
        Commit();
        return item;
    }

    /// <summary>Moves a free-flow item's top-left to (<paramref name="xPt"/>, <paramref name="yPt"/>) and selects it.</summary>
    /// <param name="id">The item's module id.</param>
    /// <param name="xPt">New left edge from the canvas's left.</param>
    /// <param name="yPt">New top edge from the canvas's top.</param>
    /// <param name="coalesceKey">Moves with the same key within 1.2 s become one undo step (keyboard nudges); null is one step.</param>
    public bool MoveItem(string id, double xPt, double yPt, string? coalesceKey = null)
    {
        if (Locate(id) is not { Item: { } item })
        {
            return false;
        }

        var (x, y) = (ClampPosition(xPt), ClampPosition(yPt));
        Selection = new(CreatorSelectionKind.Module, id);
        if (x == item.XPt && y == item.YPt)
        {
            Raise();
            return false;
        }

        (item.XPt, item.YPt) = (x, y);
        Announce($"Moved to {Pt(x)}, {Pt(y)}.");
        Commit(coalesceKey);
        return true;
    }

    /// <summary>Moves a free-flow item by (<paramref name="dxPt"/>, <paramref name="dyPt"/>); held arrow keys make one undo step.</summary>
    public bool NudgeItem(string id, double dxPt, double dyPt) =>
        Locate(id) is { Item: { } item } && MoveItem(id, item.XPt + dxPt, item.YPt + dyPt, "nudge:" + id);

    /// <summary>Sets a free-flow item's box (sizes at least <see cref="PlacedModule.MinSizePt"/>) and selects it. One undo step.</summary>
    public bool ResizeItem(string id, double xPt, double yPt, double widthPt, double heightPt)
    {
        if (Locate(id) is not { Item: { } item })
        {
            return false;
        }

        var (x, y, w, h) = (ClampPosition(xPt), ClampPosition(yPt), ClampSize(widthPt), ClampSize(heightPt));
        Selection = new(CreatorSelectionKind.Module, id);
        if (x == item.XPt && y == item.YPt && w == item.WidthPt && h == item.HeightPt)
        {
            Raise();
            return false;
        }

        (item.XPt, item.YPt, item.WidthPt, item.HeightPt) = (x, y, w, h);
        Announce($"Resized to {Pt(w)} by {Pt(h)} pt.");
        Commit();
        return true;
    }

    /// <summary>Sets a free-flow item's Z level (0 to 100). One undo step.</summary>
    public bool SetItemZ(string id, int z)
    {
        if (Locate(id) is not { Item: { } item })
        {
            return false;
        }

        z = Math.Clamp(z, 0, BoxDesign.MaxZIndex);
        if (z == item.Z)
        {
            return false;
        }

        item.Z = z;
        Announce($"Z level {z}.");
        Commit();
        return true;
    }

    /// <summary>Paints the item over the next item above it (Ctrl+]).</summary>
    public bool BringForward(string id) => Restack(id, (order, i) => i < order.Count - 1 ? (order[i + 1], true) : null, "Brought forward.");

    /// <summary>Paints the item under the next item below it (Ctrl+[).</summary>
    public bool SendBackward(string id) => Restack(id, (order, i) => i > 0 ? (order[i - 1], false) : null, "Sent backward.");

    /// <summary>Paints the item over every other item of its canvas (Ctrl+Shift+]).</summary>
    public bool BringToFront(string id) => Restack(id, (order, i) => i < order.Count - 1 ? (order[^1], true) : null, "Brought to front.");

    /// <summary>Paints the item under every other item of its canvas (Ctrl+Shift+[).</summary>
    public bool SendToBack(string id) => Restack(id, (order, i) => i > 0 ? (order[0], false) : null, "Sent to back.");

    /// <summary>
    /// Puts an item at <paramref name="frontIndex"/> in the front-to-back order of a free-flow section (the structure
    /// tree's drag), moving it there from another canvas when needed (it keeps its box). The index is the drop position
    /// as the list looks before the item is taken out.
    /// </summary>
    public bool ReorderItem(string id, string sectionId, int frontIndex)
    {
        if (Locate(id) is not { Item: { } item } source
            || Locate(sectionId) is not { Kind: CreatorSelectionKind.Section, Section: { Layout: SectionLayout.FreeFlow } target })
        {
            return false;
        }

        if (ReferenceEquals(source.Section, target))
        {
            var order = CreatorFreeFlow.FrontToBack(target);
            var current = order.IndexOf(item);
            frontIndex = Math.Clamp(frontIndex, 0, order.Count);
            if (frontIndex == current || frontIndex == current + 1)
            {
                return false;
            }

            if (frontIndex > current)
            {
                frontIndex--;
            }
        }
        else
        {
            source.Section.Items.RemoveAt(source.Index);
            target.Items.Add(item);
        }

        SetFrontIndex(target, item, frontIndex);
        Selection = new(CreatorSelectionKind.Module, id);
        var position = CreatorFreeFlow.FrontToBack(target).IndexOf(item) + 1;
        Announce($"Stacking order: {position} of {target.Items.Count} from the front.");
        Commit();
        return true;
    }

    /// <summary>
    /// Drops an existing module on a free-flow canvas at (<paramref name="xPt"/>, <paramref name="yPt"/>): an item of the
    /// same canvas moves there, an item of another canvas moves over with its size, and a module from a column becomes an
    /// item of its type's default size. One undo step.
    /// </summary>
    public bool PlaceOnCanvas(string id, string sectionId, double xPt, double yPt)
    {
        if (Locate(id) is not { Kind: CreatorSelectionKind.Module, Module: { } module } source
            || Locate(sectionId) is not { Kind: CreatorSelectionKind.Section, Section: { Layout: SectionLayout.FreeFlow } target }
            || !CreatorFreeFlow.Allowed(module))
        {
            return false;
        }

        if (source.Item is { } item)
        {
            if (ReferenceEquals(source.Section, target))
            {
                return MoveItem(id, xPt, yPt);
            }

            source.Section.Items.RemoveAt(source.Index);
            (item.XPt, item.YPt, item.Z) = (ClampPosition(xPt), ClampPosition(yPt), NextZ(target));
            target.Items.Add(item);
            Selection = new(CreatorSelectionKind.Module, id);
        }
        else
        {
            source.Column!.Modules.RemoveAt(source.Index);
            PlaceNewSelected(target, module, xPt, yPt);
        }

        Announce($"{CreatorModuleCatalog.NameOf(module)} module placed on the canvas.");
        Commit();
        return true;
    }

    private Section NewSection() => Working?.DefaultLayout == SectionLayout.FreeFlow
        ? new Section { Layout = SectionLayout.FreeFlow, Rows = [] }
        : new Section { Rows = [new Row()] };

    /// <summary>Adds an item in front of the others and selects it (no commit).</summary>
    private static PlacedModule PlaceNew(Section section, Module module, double xPt, double yPt, double? widthPt = null, double? heightPt = null)
    {
        var (w, h) = CreatorFreeFlow.DefaultSize(module);
        var item = new PlacedModule
        {
            Module = module,
            XPt = ClampPosition(xPt),
            YPt = ClampPosition(yPt),
            WidthPt = ClampSize(widthPt ?? w),
            HeightPt = ClampSize(heightPt ?? h),
            Z = NextZ(section),
            LockAspect = module is ImageModule,
        };
        section.Items.Add(item);
        return item;
    }

    private PlacedModule PlaceNewSelected(Section section, Module module, double xPt, double yPt)
    {
        var item = PlaceNew(section, module, xPt, yPt);
        Selection = new(CreatorSelectionKind.Module, module.Id);
        return item;
    }

    /// <summary>A click insert onto a canvas: the next spot of a gentle cascade from the top-left.</summary>
    private void AddItemCascaded(Section section, Module module)
    {
        PlaceNewSelected(section, module, CascadeX(section), CascadeY(section));
        Announce($"{CreatorModuleCatalog.NameOf(module)} module placed on the canvas. Drag it where you want it.");
        Commit();
    }

    private static double CascadeX(Section section) => 24 + (12 * (section.Items.Count % 10));

    private static double CascadeY(Section section) => 24 + (12 * (section.Items.Count % 10));

    private static int NextZ(Section section) => section.Items.Count == 0 ? 0 : Math.Min(BoxDesign.MaxZIndex, section.Items.Max(i => i.Z) + 1);

    private bool Restack(string id, Func<List<PlacedModule>, int, (PlacedModule Other, bool Above)?> pick, string message)
    {
        if (Locate(id) is not { Item: { } item } location)
        {
            return false;
        }

        var order = CreatorFreeFlow.PaintOrder(location.Section);
        if (pick(order, order.IndexOf(item)) is not { } choice)
        {
            return false;
        }

        if (choice.Above)
        {
            PlaceAbove(location.Section, item, choice.Other);
        }
        else
        {
            PlaceBelow(location.Section, item, choice.Other);
        }

        Selection = new(CreatorSelectionKind.Module, id);
        Announce(message);
        Commit();
        return true;
    }

    /// <summary>Makes <paramref name="item"/> paint directly over <paramref name="below"/>: its level, right after it in the list.</summary>
    private static void PlaceAbove(Section section, PlacedModule item, PlacedModule below)
    {
        section.Items.Remove(item);
        item.Z = below.Z;
        section.Items.Insert(section.Items.IndexOf(below) + 1, item);
    }

    /// <summary>Makes <paramref name="item"/> paint directly under <paramref name="above"/>: its level, right before it in the list.</summary>
    private static void PlaceBelow(Section section, PlacedModule item, PlacedModule above)
    {
        section.Items.Remove(item);
        item.Z = above.Z;
        section.Items.Insert(section.Items.IndexOf(above), item);
    }

    /// <summary>Puts an item (already in <paramref name="section"/>) at that index of the front-to-back order of the others.</summary>
    private static void SetFrontIndex(Section section, PlacedModule item, int frontIndex)
    {
        var others = CreatorFreeFlow.FrontToBack(section).Where(i => !ReferenceEquals(i, item)).ToList();
        if (others.Count == 0)
        {
            return;
        }

        frontIndex = Math.Clamp(frontIndex, 0, others.Count);
        if (frontIndex < others.Count)
        {
            PlaceAbove(section, item, others[frontIndex]);
        }
        else
        {
            PlaceBelow(section, item, others[^1]);
        }
    }

    private void ConvertSection(Section section, SectionLayout layout)
    {
        if (Working is null || section.Layout == layout)
        {
            return;
        }

        if (layout == SectionLayout.Structure)
        {
            SectionLayoutConverter.ToStructure(section);
            if (section.Rows.Count == 0)
            {
                section.Rows.Add(new Row());
            }

            return;
        }

        var width = CreatorFreeFlow.CanvasWidthPt(Working.Theme, section);
        PublicationBox? origin = null;
        Dictionary<string, IReadOnlyList<PublicationBox>>? boxes = null;
        if (PageMap is { } map && SectionLayoutConverter.CanvasBox(section, map.Boxes) is { } canvas)
        {
            // Modules keep their place on screen: their boxes on the section's first page, relative to the canvas's
            // top-left. Modules the render put on a later page get no box, so the converter stacks them below.
            origin = canvas;
            if (canvas.WidthPt > PlacedModule.MinSizePt)
            {
                width = canvas.WidthPt;
            }

            boxes = new Dictionary<string, IReadOnlyList<PublicationBox>>(StringComparer.Ordinal);
            foreach (var module in section.Rows.SelectMany(r => r.Columns).SelectMany(c => c.Modules))
            {
                if (map.Boxes.TryGetValue(module.Id, out var drawn) && drawn.Count > 0 && drawn[0].Page == canvas.Page)
                {
                    // A module may report one box per block: use their union on that page.
                    boxes[module.Id] = [CreatorFreeFlow.Union(drawn.Where(b => b.Page == canvas.Page))];
                }
            }
        }

        // The converter moves the modules' offsets and Z levels onto the items and gives a canvas without a height
        // the height of its content, so the document keeps its length.
        SectionLayoutConverter.ToFreeFlow(section, width, origin, boxes, Working);
    }

    private void KeepSelectionValid()
    {
        if (Selection.Id is not null && Locate(Selection.Id) is null)
        {
            Selection = CreatorSelection.None;
        }
    }

    private static double ClampPosition(double value) =>
        double.IsFinite(value) ? Math.Round(Math.Clamp(value, -PlacedModule.MaxPt, PlacedModule.MaxPt), 2) : 0;

    private static double ClampSize(double value) =>
        double.IsFinite(value) ? Math.Round(Math.Clamp(value, PlacedModule.MinSizePt, PlacedModule.MaxPt), 2) : PlacedModule.DefaultWidthPt;

    private static string Pt(double value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Replaces the publication's look with a copy of <paramref name="theme"/> (one undo step).</summary>
    public void SetTheme(PdfTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (Working is null)
        {
            return;
        }

        var copy = ThemeValidator.Normalize(theme.Clone());
        copy.IsBuiltIn = false;

        // Page size, orientation and margins, and what the header and footer say, belong to the publication (the
        // inspector edits them), not to the look: a new theme must not reset them. Colours, sizes and rules do
        // belong to the look (a dark theme needs its own page background and header colour), so those change.
        var own = Working.Theme;
        copy.Page.Size = own.Page.Size;
        copy.Page.Orientation = own.Page.Orientation;
        copy.Page.CustomWidthMm = own.Page.CustomWidthMm;
        copy.Page.CustomHeightMm = own.Page.CustomHeightMm;
        copy.Page.MarginTopMm = own.Page.MarginTopMm;
        copy.Page.MarginBottomMm = own.Page.MarginBottomMm;
        copy.Page.MarginLeftMm = own.Page.MarginLeftMm;
        copy.Page.MarginRightMm = own.Page.MarginRightMm;
        KeepText(copy.Header, own.Header);
        KeepText(copy.Footer, own.Footer);

        static void KeepText(HeaderFooterSettings target, HeaderFooterSettings source)
        {
            target.Enabled = source.Enabled;
            target.Left = source.Left;
            target.Center = source.Center;
            target.Right = source.Right;
            target.ShowOnFirstPage = source.ShowOnFirstPage;
            target.ShowOnCover = source.ShowOnCover;
        }

        Working.Theme = copy;
        Working.ThemeId = theme.Id;
        Announce($"Theme changed to {theme.Name}. Page, header and footer settings kept.");
        Commit();
    }

    /// <summary>
    /// Adds an image to the publication's assets (not an undo step on its own: assigning it to a module is) and
    /// returns it. The pixel size is read from PNG, JPEG, GIF and WebP headers when possible.
    /// </summary>
    public Task<PublicationAsset> AddAssetAsync(byte[] bytes, string fileName, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (Working is null)
        {
            throw new InvalidOperationException("No publication is open.");
        }

        int? width = null, height = null;
        if (!string.Equals(mediaType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var (w, h) = AllInOnePdf.Core.Rendering.ImageHeader.ReadPixelSize(bytes);
                (width, height) = w > 0 && h > 0 ? (w, h) : ((int?)null, (int?)null);
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException)
            {
            }
        }
        var asset = new PublicationAsset
        {
            FileName = string.IsNullOrWhiteSpace(fileName) ? "image" : Path.GetFileName(fileName),
            MediaType = mediaType,
            Data = bytes,
            PixelWidth = width,
            PixelHeight = height,
        };
        Working.Assets.Add(asset);
        _assetData[asset.Id] = bytes;
        return Task.FromResult(asset);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>JSON of the publication without identity, so equality means "same content and look".</summary>
    public static string Snapshot(Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var (id, name, created, modified) = (publication.Id, publication.Name, publication.Created, publication.Modified);
        try
        {
            publication.Id = string.Empty;
            publication.Name = string.Empty;
            publication.Created = default;
            publication.Modified = default;
            return publication.ToJson();
        }
        finally
        {
            publication.Id = id;
            publication.Name = name;
            publication.Created = created;
            publication.Modified = modified;
        }
    }

    /// <summary>Deep copy of a module with a new id.</summary>
    public static Module DeepCopy(Module module)
    {
        var copy = JsonSerializer.Deserialize<Module>(JsonSerializer.Serialize(module, Json), Json)!;
        copy.Id = Publication.NewId();
        return copy;
    }

    private static Row DeepCopy(Row row)
    {
        var copy = JsonSerializer.Deserialize<Row>(JsonSerializer.Serialize(row, Json), Json)!;
        Reidentify(copy);
        return copy;
    }

    private static Section DeepCopy(Section section)
    {
        var copy = JsonSerializer.Deserialize<Section>(JsonSerializer.Serialize(section, Json), Json)!;
        copy.Id = Publication.NewId();
        foreach (var row in copy.Rows)
        {
            Reidentify(row);
        }

        foreach (var item in copy.Items)
        {
            item.Module.Id = Publication.NewId();
        }

        return copy;
    }

    private static void Reidentify(Row row)
    {
        row.Id = Publication.NewId();
        foreach (var column in row.Columns)
        {
            column.Id = Publication.NewId();
            foreach (var module in column.Modules)
            {
                module.Id = Publication.NewId();
            }
        }
    }

    private bool MoveInList<T>(List<T> from, int fromIndex, List<T> to, int index, T item, string what)
    {
        index = Math.Clamp(index, 0, to.Count);
        if (ReferenceEquals(from, to))
        {
            if (index == fromIndex || index == fromIndex + 1)
            {
                return false;
            }

            if (index > fromIndex)
            {
                index--;
            }
        }

        from.RemoveAt(fromIndex);
        to.Insert(Math.Clamp(index, 0, to.Count), item);
        var id = item switch
        {
            Module m => m.Id,
            Row r => r.Id,
            Section s => s.Id,
            _ => null,
        };
        Selection = id is null ? Selection : new(what switch
        {
            "module" => CreatorSelectionKind.Module,
            "row" => CreatorSelectionKind.Row,
            _ => CreatorSelectionKind.Section,
        }, id);
        var position = to.IndexOf(item) + 1;
        Announce($"{char.ToUpperInvariant(what[0])}{what[1..]} moved to position {position} of {to.Count}.");
        Commit();
        return true;
    }

    private static Publication PrepareForSave(Publication working)
    {
        var copy = working.Clone();

        // Drop images no module uses any more (they stay in the session's undo history).
        var used = CreatorFreeFlow.ModulesOf(copy)
            .OfType<ImageModule>().Select(i => i.AssetId).Where(a => a is not null).ToHashSet(StringComparer.Ordinal);
        copy.Assets.RemoveAll(a => a is null || !used.Contains(a.Id) || a.Data.Length == 0);
        return copy;
    }

    private void RememberAssets(Publication publication)
    {
        foreach (var asset in publication.Assets)
        {
            if (asset is { Data.Length: > 0 })
            {
                _assetData[asset.Id] = asset.Data;
            }
        }
    }

    private void Restore(string json)
    {
        var restored = Publication.FromJson(json);
        if (Working is not null)
        {
            restored.Id = Working.Id;
            restored.Name = Working.Name;
            restored.Created = Working.Created;
            restored.Modified = Working.Modified;
        }

        foreach (var asset in restored.Assets)
        {
            if (_assetData.TryGetValue(asset.Id, out var data))
            {
                asset.Data = data;
            }
        }

        Working = restored;
        _currentJson = json;
        _lastKey = null;
        if (Selection.Id is not null && Locate(Selection.Id) is null)
        {
            Selection = CreatorSelection.None;
        }

        ContentVersion++;
        Raise(edited: true);
    }

    private void Announce(string message) => Announcement = message;

    private static string Columns(int count) => count == 1 ? "one column" : $"{count} columns";

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
            SchedulePageMap();
        }

        Changed?.Invoke();
    }
}
