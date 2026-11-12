using System.IO.Compression;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Content;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Rendering;
using AllInOnePdf.Publishing.Serialization;
using AllInOnePdf.Publishing.Storage;
using AllInOnePdf.Publishing.Templates;
using AllInOnePdf.UI.Components.Creator;
using AllInOnePdf.UI.Components.ThemeBuilder;
using AllInOnePdf.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace AllInOnePdf.UI.Pages;

/// <summary>
/// The Creator's builder (<c>/create/{id}</c>; <c>/create/new:&lt;templateId&gt;</c> starts from a template): structure
/// and insert on the left, the design canvas or the PDF preview in the middle, the inspector on the right.
/// </summary>
public partial class CreatorBuilder
{
    /// <summary>Route prefix that creates a publication from a template.</summary>
    public const string NewPrefix = "new:";

    /// <summary>Publication id, or <c>new:&lt;templateId&gt;</c>.</summary>
    [Parameter] public string Id { get; set; } = string.Empty;

    [Inject] private IPublicationStore Store { get; set; } = default!;
    [Inject] private CreatorState State { get; set; } = default!;
    [Inject] private CreatorThumbnailCache Thumbnails { get; set; } = default!;
    [Inject] private IThemeStore Themes { get; set; } = default!;
    [Inject] private FormatRegistry Formats { get; set; } = default!;
    [Inject] private AppState App { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IDialogService Dialogs { get; set; } = default!;
    [Inject] private IToastService Toasts { get; set; } = default!;
    [Inject] private IHostServices Host { get; set; } = default!;
    [Inject] private IPdfRasterizer Rasterizer { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private static readonly (string FormatId, string Label)[] ExportCandidates =
    [
        ("docx", "Export as Word (.docx)"),
        ("xlsx", "Export as Excel (.xlsx)"),
        ("pptx", "Export as PowerPoint (.pptx)"),
        ("html", "Export as HTML"),
        ("epub", "Export as EPUB"),
        ("markdown", "Export as Markdown"),
        ("text", "Export as plain text"),
        ("png", "Export as PNG images"),
        ("jpeg", "Export as JPEG images"),
        ("webp", "Export as WebP images"),
    ];

    /// <summary>Image exports rasterise the real publication PDF (not the flattened document), one image per page.</summary>
    private static readonly HashSet<string> ImageFormats = new(StringComparer.Ordinal) { "png", "jpeg", "webp" };

    /// <summary>Resolution of exported page images.</summary>
    private const int ImageExportDpi = 150;

    private static readonly (string Value, string Label)[] ZoomLevels =
    [
        ("fit", "Fit width"),
        ("0.5", "50%"),
        ("0.75", "75%"),
        ("1", "100%"),
        ("1.25", "125%"),
        ("1.5", "150%"),
    ];

    private static readonly (SectionLayout Layout, string Label, Icon Icon, string Tip)[] LayoutChoices =
    [
        (SectionLayout.Structure, "Structure", new Icons.Regular.Size16.LayoutColumnTwo(), "Rows and columns that flow from page to page"),
        (SectionLayout.FreeFlow, "Free flow", new Icons.Regular.Size16.Grid(), "A canvas where modules stay wherever you drag them"),
    ];

    private string _mode = "design";
    private string? _inspectorTab;
    private int _inspectorTabRequest;
    private string _zoom = "fit";
    private string _leftTab = "structure";
    private bool _leftCollapsed;
    private bool _rightCollapsed;
    private bool _leftDrawer;
    private bool _rightDrawer;
    private bool _moreOpen;
    private bool _saving;
    private bool _exporting;
    private bool _narrow;
    private bool _notFound;
    private bool _allowNavigation;
    private bool? _jsDirty;
    private string? _loadedFor;
    private DesignCanvas? _canvas;
    private IDisposable? _navGuard;
    private IJSObjectReference? _module;
    private DotNetObjectReference<CreatorBuilder>? _self;

    private bool CanReset => !State.IsNew && State.IsDirty;

    private IEnumerable<(string FormatId, string Label)> ExportFormats =>
        ExportCandidates.Where(c => Formats.FindExporter(c.FormatId) is { Status: not ConverterStatus.Planned });

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        State.Changed += OnStateChanged;
        State.PruneOldDrafts();
    }

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (string.Equals(_loadedFor, Id, StringComparison.Ordinal))
        {
            return;
        }

        _loadedFor = Id;
        _notFound = false;

        if (State.Working is not null && string.Equals(State.LoadedId, Id, StringComparison.Ordinal))
        {
            // Same publication (back from the Reader, or right after the first save): keep the session and its history.
            return;
        }

        if (Id.StartsWith(NewPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var template = BuiltInTemplates.Find(Id[NewPrefix.Length..]) ?? BuiltInTemplates.Find(BuiltInTemplates.BlankId)!;
            State.Load(template.Create(), isNew: true, routeId: Id);
            await State.CheckForDraftAsync();
            return;
        }

        var stored = await Store.GetAsync(Id);
        if (stored is null)
        {
            _notFound = true;
            return;
        }

        State.Load(stored, isNew: false);
        await State.CheckForDraftAsync();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _navGuard = Nav.RegisterLocationChangingHandler(OnLocationChanging);
            try
            {
                _module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/AllInOnePdf.UI/Pages/CreatorBuilder.razor.js");
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

    // ------------------------------------------------------------ shortcuts

    /// <summary>Called by the page script for keyboard shortcuts.</summary>
    [JSInvokable]
    public Task OnShortcut(string action) => InvokeAsync(async () =>
    {
        var id = State.Selection.Id;
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
            case "delete" when id is not null && _mode == "design":
                State.Delete(id);
                await FocusSelectionAsync();
                break;
            case "duplicate" when id is not null && _mode == "design":
                State.Duplicate(id);
                await FocusSelectionAsync();
                break;
            case "up" or "down" when id is not null && _mode == "design":
                if (State.MoveBy(id, action == "up" ? -1 : 1))
                {
                    await FocusSelectionAsync();
                }

                break;
            case "forward" or "backward" or "front" or "back" when id is not null && _mode == "design" && State.FindItem(id) is not null:
                _ = action switch
                {
                    "forward" => State.BringForward(id),
                    "backward" => State.SendBackward(id),
                    "front" => State.BringToFront(id),
                    _ => State.SendToBack(id),
                };
                break;
            case not null when action.StartsWith("nudge:", StringComparison.Ordinal) && id is not null && _mode == "design":
            {
                var parts = action.Split(':');
                if (parts.Length == 3
                    && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dx)
                    && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dy))
                {
                    State.NudgeItem(id, dx, dy);
                }

                break;
            }
            case "escape":
                if (_leftDrawer || _rightDrawer)
                {
                    CloseDrawers();
                }
                else
                {
                    State.ClearSelection();
                }

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
            CloseDrawers();
            StateHasChanged();
        }
    });

    private async Task FocusSelectionAsync()
    {
        StateHasChanged();
        await Task.Yield();
        if (_canvas is not null)
        {
            await _canvas.FocusAsync(State.Selection.Id);
        }
    }

    // --------------------------------------------------------------- panels

    private void ToggleDrawer(bool left)
    {
        if (left)
        {
            _leftDrawer = !_leftDrawer;
            _rightDrawer = false;
        }
        else
        {
            _rightDrawer = !_rightDrawer;
            _leftDrawer = false;
        }
    }

    private void CloseDrawers()
    {
        _leftDrawer = false;
        _rightDrawer = false;
    }

    private void OpenInsertFor(string columnId)
    {
        State.Select(CreatorSelectionKind.Column, columnId);
        _leftTab = "insert";
        _leftCollapsed = false;
        if (_narrow)
        {
            _leftDrawer = true;
            _rightDrawer = false;
        }
    }

    private void OpenInspector()
    {
        _rightCollapsed = false;
        if (_narrow)
        {
            _rightDrawer = true;
            _leftDrawer = false;
        }
    }

    /// <summary>A double-click on a free-flow item: the inspector opens on its Content tab.</summary>
    private void EditContent(string id)
    {
        _inspectorTab = "content";
        _inspectorTabRequest++;
        OpenInspector();
    }

    /// <summary>
    /// The "Structure | Free flow" switch: sets what new sections get. When sections use the other layout, asks
    /// whether to convert them too (Convert all / Only new sections / Cancel).
    /// </summary>
    private async Task SwitchLayoutAsync(SectionLayout layout)
    {
        if (State.Working is not { } p || p.DefaultLayout == layout)
        {
            return;
        }

        var others = p.Sections.Count(s => s.Layout != layout);
        if (others == 0)
        {
            State.SetDefaultLayout(layout, convertExisting: false);
            return;
        }

        var dialog = await Dialogs.ShowDialogAsync<LayoutSwitchDialog>(new LayoutSwitchModel { Layout = layout, OtherSections = others }, new DialogParameters
        {
            Width = "460px",
            Modal = true,
            TrapFocus = true,
            PrimaryAction = null,
            SecondaryAction = null,
            AriaLabelledby = "cr-layout-switch-title",
            AriaDescribedby = "cr-layout-switch-desc",
            DialogType = DialogType.Dialog,
        });
        var result = await dialog.Result;
        var choice = result.Cancelled ? null : result.Data as string;
        if (choice is LayoutSwitchDialog.ConvertAllResult or LayoutSwitchDialog.NewOnlyResult)
        {
            State.SetDefaultLayout(layout, convertExisting: choice == LayoutSwitchDialog.ConvertAllResult);
        }
    }

    private async Task OnTreePickedAsync(string id)
    {
        if (_narrow)
        {
            _leftDrawer = false;
        }

        if (_canvas is not null)
        {
            await _canvas.RevealAsync(id);
        }
    }

    private string SelectionText()
    {
        var location = State.Locate(State.Selection.Id);
        return location switch
        {
            null => "Nothing selected: the inspector shows the document settings",
            { Kind: CreatorSelectionKind.Module, Module: { } m } => $"{CreatorModuleCatalog.NameOf(m)} module selected",
            { Kind: var k } => $"{k} selected",
        };
    }

    // ---------------------------------------------------------------- theme

    private async Task ChangeThemeAsync()
    {
        if (State.Working is null)
        {
            return;
        }

        var model = new ThemePickerModel { CurrentId = State.Working.ThemeId };
        var dialog = await Dialogs.ShowDialogAsync<ThemePickerDialog>(model, new DialogParameters
        {
            Title = "Choose a theme",
            PrimaryAction = "Use theme",
            SecondaryAction = "Cancel",
            Width = "760px",
            TrapFocus = true,
            Modal = true,
        });
        var result = await dialog.Result;
        if (result.Cancelled || string.IsNullOrEmpty(model.SelectedId))
        {
            return;
        }

        var theme = await Themes.GetAsync(model.SelectedId);
        if (theme is null)
        {
            Toasts.ShowError("That theme is no longer in the gallery. Pick another one.");
            return;
        }

        if (model.SelectedId == State.Working.ThemeId && string.Equals(theme.Name, State.Working.Theme.Name, StringComparison.Ordinal)
            && string.Equals(ThemeJson(theme), ThemeJson(State.Working.Theme), StringComparison.Ordinal))
        {
            return;
        }

        State.SetTheme(theme);
        Toasts.ShowInfo($"The publication now uses {theme.Name}. Undo to go back.");
    }

    private static string ThemeJson(PdfTheme theme) => ThemeBuilderState.Snapshot(theme);

    // ----------------------------------------------------------------- save

    private async Task SaveAsync()
    {
        if (State.Working is null || _saving)
        {
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
                Nav.NavigateTo($"/create/{Uri.EscapeDataString(State.Working.Id)}", replace: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Toasts.ShowError($"The publication could not be saved: {ex.Message} Check that the data folder is writable and try again.");
        }
        finally
        {
            _saving = false;
            _allowNavigation = false;
        }
    }

    /// <summary>Saves the working copy as a new publication (name asked in a dialog) and continues editing the copy.</summary>
    private async Task SaveAsAsync()
    {
        if (State.Working is null || _saving)
        {
            return;
        }

        var model = State.IsNew
            ? new SaveAsPublicationModel { Name = State.Working.Name, NeverSaved = true }
            : new SaveAsPublicationModel { Name = State.Working.Name + " copy" };
        var dialog = await Dialogs.ShowDialogAsync<SaveAsPublicationDialog>(model, new DialogParameters
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

        _saving = true;
        try
        {
            var copy = await State.SaveAsCopyAsync(model.Name.Trim());
            Toasts.ShowSuccess($"Saved as \"{copy.Name}\".");
            _loadedFor = copy.Id;
            _allowNavigation = true;
            Nav.NavigateTo($"/create/{Uri.EscapeDataString(copy.Id)}", replace: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Toasts.ShowError($"The copy could not be saved: {ex.Message} Check that the data folder is writable and try again.");
        }
        finally
        {
            _saving = false;
            _allowNavigation = false;
        }
    }

    // --------------------------------------------------------------- export

    private string BaseName => ConversionService.SanitizeBaseName(State.Working?.Name ?? "Publication");

    private async Task ExportPdfAsync()
    {
        if (State.Working is null || _exporting)
        {
            return;
        }

        _exporting = true;
        try
        {
            var pdf = await Thumbnails.RenderPdfAsync(State.Working.Clone(), new PublicationRenderOptions { EmbedMetadata = true });
            await Host.SaveFileAsync(BaseName + ".pdf", pdf, "application/pdf");
        }
        catch (CreatorRendererMissingException)
        {
            Toasts.ShowError("The PDF could not be created: the PDF renderer is not available in this build. Export as another format instead.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Toasts.ShowError($"The PDF could not be created: {ex.Message} Check the publication in Preview and try again.");
        }
        finally
        {
            _exporting = false;
        }
    }

    private async Task ExportAsAsync(string formatId)
    {
        if (State.Working is null || _exporting || Formats.FindExporter(formatId) is not { } exporter)
        {
            return;
        }

        if (ImageFormats.Contains(formatId))
        {
            await ExportImagesAsync(formatId, exporter.Format);
            return;
        }

        _exporting = true;
        try
        {
            var document = PublicationFlattener.ToDocument(State.Working.Clone());
            var result = await Task.Run(() => exporter.ExportAsync(new ExportContext { Document = document, BaseName = BaseName, Theme = State.Working.Theme }));
            if (result.Files.Count == 0)
            {
                Toasts.ShowError($"The {exporter.Format.DisplayName} export produced no file. Add some content and try again.");
                return;
            }

            if (result.Files.Count == 1 && result.Primary is { } single)
            {
                await Host.SaveFileAsync(single.FileName, single.Content, single.MediaType);
            }
            else
            {
                // Several files (for example Markdown plus its images): hand them over as one zip.
                using var buffer = new MemoryStream();
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var file in result.Files)
                    {
                        var entry = zip.CreateEntry(file.FileName, CompressionLevel.Optimal);
                        await using var stream = entry.Open();
                        await stream.WriteAsync(file.Content);
                    }
                }

                await Host.SaveFileAsync(BaseName + "-" + exporter.Format.Id + ".zip", buffer.ToArray(), "application/zip");
            }

            foreach (var warning in result.Warnings.Take(3))
            {
                Toasts.ShowWarning(warning);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Toasts.ShowError($"The {exporter.Format.DisplayName} export failed: {ex.Message}");
        }
        finally
        {
            _exporting = false;
        }
    }

    /// <summary>Page images of the real publication PDF: one file for a single page, otherwise a zip.</summary>
    private async Task ExportImagesAsync(string formatId, FileFormat format)
    {
        if (State.Working is null)
        {
            return;
        }

        var encoding = formatId switch { "jpeg" => ImageEncoding.Jpeg, "webp" => ImageEncoding.WebP, _ => ImageEncoding.Png };
        var extension = format.Extensions.Count > 0 ? format.Extensions[0] : "." + formatId;
        _exporting = true;
        try
        {
            var pdf = await Thumbnails.RenderPdfAsync(State.Working.Clone(), new PublicationRenderOptions { EmbedMetadata = false });
            var count = Rasterizer.GetPageCount(pdf);
            var pages = new List<PageImage>(count);
            for (var i = 0; i < count; i++)
            {
                pages.Add(await Rasterizer.RasterizePageAsync(pdf, i, new RasterizeOptions { Dpi = ImageExportDpi, Encoding = encoding, JpegQuality = 90, WithAnnotations = false }));
            }

            if (pages.Count == 1)
            {
                await Host.SaveFileAsync($"{BaseName}{extension}", pages[0].Data, pages[0].MediaType);
                return;
            }

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (var i = 0; i < pages.Count; i++)
                {
                    var entry = zip.CreateEntry($"{BaseName}-p{i + 1}{extension}", CompressionLevel.Fastest);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(pages[i].Data);
                }
            }

            await Host.SaveFileAsync($"{BaseName}-{formatId}.zip", buffer.ToArray(), "application/zip");
        }
        catch (CreatorRendererMissingException)
        {
            Toasts.ShowError("The page images could not be created: the PDF renderer is not available in this build.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Toasts.ShowError($"The {format.DisplayName} export failed: {ex.Message}");
        }
        finally
        {
            _exporting = false;
        }
    }

    private async Task SavePackageAsync()
    {
        if (State.Working is null || _exporting)
        {
            return;
        }

        _exporting = true;
        try
        {
            var copy = State.Working.Clone();
            var thumbnail = await Thumbnails.TryRenderThumbnailPngAsync(copy);
            var bytes = PublicationPackage.Write(copy, thumbnail);
            await Host.SaveFileAsync(BaseName + PublicationPackage.Extension, bytes, PublicationPackage.MediaType);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JSException or InvalidOperationException)
        {
            Toasts.ShowError($"The package could not be saved: {ex.Message}");
        }
        finally
        {
            _exporting = false;
        }
    }

    private void ReadInReader()
    {
        if (State.Working is null)
        {
            return;
        }

        var document = PublicationFlattener.ToDocument(State.Working.Clone());
        App.SetDocument(document);

        // The session stays in memory: coming back to this publication restores the edits and the history.
        _allowNavigation = true;
        Nav.NavigateTo("/read");
    }

    // ------------------------------------------------------ leaving the page

    private async ValueTask OnLocationChanging(LocationChangingContext context)
    {
        if (_allowNavigation || !State.IsDirty || State.Working is null)
        {
            return;
        }

        if (State.IsNew && !State.HasEdits)
        {
            // A template nobody touched: nothing to lose and no draft to offer, so leave without asking.
            State.Forget();
            return;
        }

        var model = new UnsavedChangesModel { Name = State.Working.Name, SaveLabel = "Save and leave" };
        var dialog = await Dialogs.ShowDialogAsync<UnsavedChangesDialog>(model, new DialogParameters
        {
            Width = "440px",
            Modal = true,
            TrapFocus = true,
            PreventDismissOnOverlayClick = true,
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

    /// <summary>"Save and leave": saves in place without navigating; false when the save failed (the user stays).</summary>
    private async Task<bool> SaveBeforeLeavingAsync()
    {
        if (State.Working is null)
        {
            return true;
        }

        _saving = true;
        try
        {
            await State.SaveAsync();
            Toasts.ShowSuccess($"Saved \"{State.Working.Name}\".");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Toasts.ShowError($"The publication could not be saved, so you are still in the Creator: {ex.Message}");
            return false;
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>"Reset to saved" discards every change since the last save, so it asks first (Undo still brings them back).</summary>
    private async Task ResetToSavedAsync()
    {
        if (!CanReset || State.Working is null)
        {
            return;
        }

        var dialog = await Dialogs.ShowConfirmationAsync(
            $"Discard every change to \"{State.Working.Name}\" since it was last saved? Undo brings them back while the Creator stays open.",
            "Reset to saved",
            "Keep changes",
            "Reset to saved");
        var result = await dialog.Result;
        if (!result.Cancelled)
        {
            State.ResetToSaved();
        }
    }

    /// <inheritdoc />
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
