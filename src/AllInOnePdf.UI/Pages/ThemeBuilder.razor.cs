using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.UI.Components.ThemeBuilder;
using AllInOnePdf.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AllInOnePdf.UI.Pages;

/// <summary>
/// The theme builder (<c>/themes/{id}</c>, <c>/themes/new</c>): preview on the left (fast HTML or exact PDF),
/// settings on the right, click an element in the preview to jump to its settings.
/// </summary>
public partial class ThemeBuilder
{
    public const string NewThemeId = "new";

    [Parameter] public string ThemeId { get; set; } = string.Empty;

    [Inject] private IThemeStore Store { get; set; } = default!;
    [Inject] private ThemeBuilderState State { get; set; } = default!;
    [Inject] private ThemeBuilderSamples Samples { get; set; } = default!;
    [Inject] private AppState App { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IDialogService Dialogs { get; set; } = default!;
    [Inject] private IToastService Toasts { get; set; } = default!;
    [Inject] private IHostServices Host { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private Document? _document;
    private string _documentKey = "Welcome";
    private ThemeBuilderSample _sample = ThemeBuilderSample.Welcome;
    private string _mode = "fast";
    private string _zoom = "fit";
    private string? _settingsQuery;
    private string? _openGroup = "typography";
    private string? _openSub;
    private string? _selectionLabel;
    private string? _announce;
    private int? _exactPages;
    private bool _panelCollapsed;
    private bool _drawerOpen;
    private bool _saving;
    private bool _narrow;
    private bool _notFound;
    private string? _loadedFor;
    private bool _allowNavigation;
    private bool? _jsDirty;
    private BuilderPreview? _preview;
    private ElementReference _bar;
    private bool _barObserved;
    private IDisposable? _navGuard;
    private IJSObjectReference? _module;
    private DotNetObjectReference<ThemeBuilder>? _self;

    private bool CanReset => !State.IsNew && State.IsDirty;

    protected override void OnInitialized()
    {
        State.Changed += OnStateChanged;
        State.PruneOldDrafts();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (string.Equals(_loadedFor, ThemeId, StringComparison.Ordinal))
        {
            return;
        }

        _loadedFor = ThemeId;
        _notFound = false;
        _exactPages = null;

        if (string.Equals(ThemeId, NewThemeId, StringComparison.OrdinalIgnoreCase))
        {
            var fresh = BuiltInThemes.Clean().Duplicate("Untitled theme");
            fresh.Description = null;
            fresh.Author = null;
            fresh.Tags = [];
            State.Load(fresh, isNew: true);
            _openGroup = "details";
            await State.CheckForDraftAsync();
        }
        else if (State.Working is not null && string.Equals(State.LoadedId, ThemeId, StringComparison.Ordinal) && !State.IsNew)
        {
            // Same theme (for example right after "Save as" navigated here): keep the session and its history.
        }
        else
        {
            var stored = await Store.GetAsync(ThemeId);
            if (stored is null)
            {
                _notFound = true;
                return;
            }

            State.Load(stored, isNew: false);
            await State.CheckForDraftAsync();
        }

        _document ??= await LoadSampleAsync(_sample);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _navGuard = Nav.RegisterLocationChangingHandler(OnLocationChanging);
            try
            {
                _module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/AllInOnePdf.UI/Pages/ThemeBuilder.razor.js");
                _self = DotNetObjectReference.Create(this);
                var narrow = await _module.InvokeAsync<bool>("init", _self);
                if (narrow != _narrow)
                {
                    _narrow = narrow;
                    StateHasChanged();
                }
            }
            catch (JSDisconnectedException)
            {
                return;
            }
            catch (JSException)
            {
                // Keyboard shortcuts are an enhancement; the buttons still work.
            }
        }

        if (_module is not null && !_barObserved && _bar.Context is not null)
        {
            _barObserved = true;
            try
            {
                await _module.InvokeVoidAsync("observeBar", _bar);
            }
            catch (JSDisconnectedException)
            {
            }
            catch (JSException)
            {
                // Without the observer the drawer opens at the default offset.
            }
        }

        if (_module is not null && _jsDirty != State.IsDirty)
        {
            _jsDirty = State.IsDirty;
            try
            {
                await _module.InvokeVoidAsync("setDirty", State.IsDirty);
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    private void OnStateChanged() => _ = InvokeAsync(StateHasChanged);

    // ------------------------------------------------------------------ edits

    // The name is identity, not part of the undo history (see ThemeBuilderState).
    private void Rename(string? name) => State.Rename(name ?? string.Empty);

    private void Undo() => State.Undo();

    private void Redo() => State.Redo();

    private void ResetToSaved() => State.ResetToSaved();

    private void RestoreDraft()
    {
        State.RestoreDraft();
        _announce = "The recovered changes were restored. Undo to go back.";
    }

    private async Task DiscardDraftAsync()
    {
        await State.DiscardDraftAsync();
        _announce = "The recovered changes were discarded.";
    }

    private void ApplyPreset(string id)
    {
        if (BuiltInThemes.Find(id) is not { } preset)
        {
            return;
        }

        State.ApplyPreset(preset);
        var message = $"Applied the settings of {preset.Name}. The name, description, author and tags did not change. Undo to go back.";
        _announce = message;
        Toasts.ShowInfo(message);
    }

    // ------------------------------------------------------- settings sections

    /// <summary>One search for the section list and the panel; a search that hides the open group opens the first match.</summary>
    private void OnSettingsQuery(string? query)
    {
        _settingsQuery = query;
        var visible = SettingsPanel.Visible(query);
        if (!string.IsNullOrWhiteSpace(query) && visible.Count > 0 && visible.All(g => g.Key != _openGroup))
        {
            _openGroup = visible[0].Key;
        }
    }

    /// <summary>A pick in the section list (wide windows): shows that group and the panel, never toggles it closed.</summary>
    private void OpenSection(string? key)
    {
        _openGroup = key;
        _panelCollapsed = false;
    }

    [JSInvokable]
    public Task OnShortcut(string action) => InvokeAsync(async () =>
    {
        switch (action)
        {
            case "undo":
                State.Undo();
                break;
            case "redo":
                State.Redo();
                break;
            case "save":
                await SaveAsync();
                break;
        }
    });

    /// <summary>Called by the page script when the window crosses the 1100 px breakpoint.</summary>
    [JSInvokable]
    public Task OnNarrowChanged(bool narrow) => InvokeAsync(() =>
    {
        if (_narrow != narrow)
        {
            _narrow = narrow;
            StateHasChanged();
        }
    });

    private void OnPreviewSelect(PreviewSelection selection)
    {
        _openGroup = selection.Group;
        if (selection.Sub is not null && selection.Group is "headings" or "headerfooter" or "callouts")
        {
            _openSub = selection.Sub;
        }

        _selectionLabel = selection.Label;
        _panelCollapsed = false;
        _drawerOpen = true;
        _announce = $"{selection.Label} selected. {SettingsPanel.TitleOf(selection.Group)} settings are open.";
    }

    // ---------------------------------------------------------------- samples

    private async Task ChangeSampleAsync(string? value)
    {
        if (!Enum.TryParse<ThemeBuilderSample>(value, out var sample) || sample == _sample)
        {
            return;
        }

        try
        {
            var doc = await LoadSampleAsync(sample);
            _sample = sample;
            _document = doc;
            _documentKey = sample == ThemeBuilderSample.Current ? "Current:" + (App.CurrentDocumentPath ?? doc.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture)) : sample.ToString();
            _exactPages = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Toasts.ShowError($"The sample could not be loaded: {ex.Message}");
        }
    }

    private Task<Document> LoadSampleAsync(ThemeBuilderSample sample) => Samples.GetAsync(sample);

    // ----------------------------------------------------------- save/export

    private async Task SaveAsync()
    {
        if (State.Working is null || _saving)
        {
            return;
        }

        if (State.IsReadOnly)
        {
            var name = State.Working.Name;
            if (BuiltInThemes.Find(State.Working.Id) is { } builtIn && string.Equals(builtIn.Name, name, StringComparison.Ordinal))
            {
                name += " copy";
            }

            await SaveCopyAsync(name);
            return;
        }

        _saving = true;
        try
        {
            var wasNew = State.IsNew;
            await State.SaveAsync();
            Toasts.ShowSuccess($"Saved \"{State.Working.Name}\".");
            if (wasNew)
            {
                _loadedFor = State.Working.Id;
                _allowNavigation = true;
                Nav.NavigateTo($"/themes/{Uri.EscapeDataString(State.Working.Id)}", replace: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Toasts.ShowError($"The theme could not be saved: {ex.Message}");
        }
        finally
        {
            _saving = false;
            _allowNavigation = false;
        }
    }

    private async Task SaveAsAsync()
    {
        if (State.Working is null)
        {
            return;
        }

        var model = new SaveAsModel { Name = State.Working.Name + " copy" };
        var dialog = await Dialogs.ShowDialogAsync<SaveAsDialog>(model, new DialogParameters
        {
            Title = "Save as",
            PrimaryAction = "Save",
            SecondaryAction = "Cancel",
            Width = "420px",
            TrapFocus = true,
            Modal = true,
        });
        var result = await dialog.Result;
        if (result.Cancelled || string.IsNullOrWhiteSpace(model.Name))
        {
            return;
        }

        await SaveCopyAsync(model.Name.Trim());
    }

    private async Task SaveCopyAsync(string name)
    {
        _saving = true;
        try
        {
            var copy = await State.SaveAsCopyAsync(name);
            Toasts.ShowSuccess($"Saved as \"{copy.Name}\".");
            _loadedFor = copy.Id;
            _allowNavigation = true;
            Nav.NavigateTo($"/themes/{Uri.EscapeDataString(copy.Id)}", replace: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Toasts.ShowError($"The copy could not be saved: {ex.Message}");
        }
        finally
        {
            _saving = false;
            _allowNavigation = false;
        }
    }

    private async Task ExportAsync()
    {
        if (State.Working is null)
        {
            return;
        }

        try
        {
            var export = State.Working.Clone();
            export.IsBuiltIn = false;
            var bytes = Encoding.UTF8.GetBytes(export.ToJson());
            await Host.SaveFileAsync(ConversionService.SanitizeBaseName(export.Name) + ".json", bytes, "application/json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JSException)
        {
            Toasts.ShowError($"The theme could not be exported: {ex.Message}");
        }
    }

    // ------------------------------------------------------- leaving the page

    private async ValueTask OnLocationChanging(LocationChangingContext context)
    {
        if (_allowNavigation || !State.IsDirty || State.Working is null)
        {
            return;
        }

        if (State.IsNew && !State.HasEdits)
        {
            // A new theme nobody touched: nothing to lose, so leave without asking.
            State.Forget();
            return;
        }

        var model = new UnsavedChangesModel
        {
            Name = State.Working.Name,
            SaveLabel = State.IsReadOnly ? "Save a copy and leave" : "Save and leave",
        };
        var dialog = await Dialogs.ShowDialogAsync<UnsavedChangesDialog>(model, new DialogParameters
        {
            Width = "440px",
            Modal = true,
            TrapFocus = true,
            PreventDismissOnOverlayClick = true,
            // The dialog renders its own footer (Keep editing is primary there).
            PrimaryAction = null,
            SecondaryAction = null,
            AriaLabelledby = "tb-unsaved-title",
            AriaDescribedby = "tb-unsaved-desc",
            DialogType = DialogType.Dialog,
        });
        var result = await dialog.Result;
        var choice = result.Cancelled ? null : result.Data as string;
        if (string.Equals(choice, UnsavedChangesDialog.SaveResult, StringComparison.Ordinal))
        {
            if (!await SaveBeforeLeavingAsync())
            {
                context.PreventNavigation();
            }

            return;
        }

        if (!string.Equals(choice, UnsavedChangesDialog.LeaveResult, StringComparison.Ordinal))
        {
            context.PreventNavigation();
            return;
        }

        State.Forget();
    }

    /// <summary>"Save and leave": saves in place (a copy for a built-in theme) without navigating; false when it failed.</summary>
    private async Task<bool> SaveBeforeLeavingAsync()
    {
        if (State.Working is null)
        {
            return true;
        }

        _saving = true;
        try
        {
            if (State.IsReadOnly)
            {
                var name = State.Working.Name;
                if (BuiltInThemes.Find(State.Working.Id) is { } builtIn && string.Equals(builtIn.Name, name, StringComparison.Ordinal))
                {
                    name += " copy";
                }

                var copy = await State.SaveAsCopyAsync(name);
                Toasts.ShowSuccess($"Saved as \"{copy.Name}\".");
            }
            else
            {
                await State.SaveAsync();
                Toasts.ShowSuccess($"Saved \"{State.Working.Name}\".");
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Toasts.ShowError($"The theme could not be saved, so the builder stays open: {ex.Message}");
            return false;
        }
        finally
        {
            _saving = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        State.Changed -= OnStateChanged;
        _navGuard?.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _self?.Dispose();
        GC.SuppressFinalize(this);
    }
}
