using AllInOnePdf.Core;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.UI.Components.Reader;
using AllInOnePdf.UI.Services;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace AllInOnePdf.UI.Pages;

/// <summary>
/// The Reader (<c>/read</c>): shows <see cref="AppState.CurrentDocument"/> in a reflowed reading view or as print
/// preview pages. The reading defaults live in <see cref="UserPreferences"/> (<c>Reader*</c>, set in Settings);
/// adjustments made here and other session-only state (recent files, scroll positions, caches) live in
/// <see cref="ReaderSession"/> and are never written back to the preferences.
/// </summary>
public partial class Reader
{
    private const string ModulePath = "./_content/AllInOnePdf.UI/Pages/Reader.razor.js";
    private const string OpenButtonId = "rd-open";


    /// <summary>Desktop deep link: <c>/read?open=&lt;absolute path&gt;</c>. Ignored on the web host.</summary>
    [SupplyParameterFromQuery(Name = "open")]
    public string? OpenPath { get; set; }

    [Inject] private AppState AppState { get; set; } = default!;
    [Inject] private IPreferencesService Preferences { get; set; } = default!;
    [Inject] private ThemeService Theme { get; set; } = default!;
    [Inject] private ReaderSession Session { get; set; } = default!;
    [Inject] private IDocumentHtmlRenderer HtmlRenderer { get; set; } = default!;
    [Inject] private IConversionService Conversion { get; set; } = default!;
    [Inject] private FormatRegistry Registry { get; set; } = default!;
    [Inject] private IHostServices Host { get; set; } = default!;
    [Inject] private ConvertWorkspace Workspace { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<Reader> Logger { get; set; } = default!;

    private ElementReference _root;
    private IJSObjectReference? _module;
    private IJSObjectReference? _handle;
    private DotNetObjectReference<Reader>? _self;
    private ReaderSurface? _surface;
    private ReaderPages? _pages;
    private ReaderToolbar? _toolbar;

    private Document? _doc;
    private string? _docPath;
    private string _title = "Untitled";
    private string? _language;
    private string? _scrollKey;
    private ReaderRenderResult? _rendered;
    private bool _renderFailed;
    private IReadOnlyList<ReaderOutlineNode> _outline = [];
    private int _words;
    private string? _activeId;
    private double? _restoreFraction;

    private ReaderViewSettings _settings = new();
    private ReaderViewSettings? _lastDefaults;
    private bool _lastPaletteChosenDefault;
    private bool _paletteChosen;
    private ReaderMode _mode;
    private bool _focus;

    private bool _loading;
    private string? _loadingName;
    private string? _error;
    private string _errorTitle = "Couldn't open the file";
    private string? _handledOpenPath;

    private CancellationTokenSource? _importCts;
    private CancellationTokenSource? _renderCts;
    private bool _disposed;

    private bool IsDesktop => Host.Kind == HostKind.Desktop;

    /// <summary>Settings as displayed: until the user picks a palette, Dusk in dark app mode and Paper otherwise.</summary>
    private ReaderViewSettings Effective =>
        _settings with { Palette = ReaderViewSettings.EffectivePalette(_settings.Palette, _paletteChosen, Theme.IsDark) };

    private string PaletteClass => ReaderViewSettings.PaletteClass(Effective.Palette);

    private string AcceptAttribute => string.Join(",", Registry.ImportableExtensions);

    private string PageTitleText => _doc is null ? "Reader · AllInOne PDF" : $"{_title} · Reader · AllInOne PDF";

    // ---- Lifecycle ----------------------------------------------------------------------------------------------

    protected override async Task OnInitializedAsync()
    {
        _mode = Session.Mode;
        await Preferences.LoadAsync();
        _settings = Session.ResolveSettings(Preferences.Current);
        _paletteChosen = Session.ResolvePaletteChosen(Preferences.Current);
        _lastDefaults = ReaderViewSettings.FromPreferences(Preferences.Current);
        _lastPaletteChosenDefault = Preferences.Current.ReaderPaletteChosen;

        AppState.Changed += OnAppStateChanged;
        Preferences.Changed += OnPreferencesChanged;
        Theme.Changed += OnThemeChanged;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (IsDesktop && !string.IsNullOrWhiteSpace(OpenPath) && !string.Equals(OpenPath, _handledOpenPath, StringComparison.Ordinal))
        {
            _handledOpenPath = OpenPath;
            var full = TryGetFullPath(OpenPath);
            if (full is null)
            {
                ShowError("Couldn't open the file", $"\"{OpenPath}\" is not a valid file path.");
            }
            else if (!FilePaths.AreEqual(AppState.CurrentDocumentPath, full) || AppState.CurrentDocument is null)
            {
                await OpenPathAsync(full);
                return;
            }
        }

        if (AppState.CurrentDocument is { } current && !ReferenceEquals(current, _doc))
        {
            await ShowDocumentAsync(current, AppState.CurrentDocumentPath);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _disposed)
        {
            return;
        }

        try
        {
            _self = DotNetObjectReference.Create(this);
            _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            _handle = await _module.InvokeAsync<IJSObjectReference>("attach", _root, _self);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Reader script failed to load");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        AppState.Changed -= OnAppStateChanged;
        Preferences.Changed -= OnPreferencesChanged;
        Theme.Changed -= OnThemeChanged;
        _importCts?.Cancel();
        _renderCts?.Cancel();


        try
        {
            if (_handle is not null)
            {
                await _handle.InvokeVoidAsync("dispose");
                await _handle.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (JSException)
        {
        }

        _self?.Dispose();
        _importCts?.Dispose();
        _renderCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- Documents ----------------------------------------------------------------------------------------------

    private void OnAppStateChanged()
    {
        _ = InvokeAsync(async () =>
        {
            if (_disposed)
            {
                return;
            }

            var current = AppState.CurrentDocument;
            if (ReferenceEquals(current, _doc))
            {
                return;
            }

            if (current is null)
            {
                ClearDocument();
                StateHasChanged();
            }
            else
            {
                await ShowDocumentAsync(current, AppState.CurrentDocumentPath);
            }
        });
    }

    private void ClearDocument()
    {
        _renderCts?.Cancel();
        _doc = null;
        _docPath = null;
        _rendered = null;
        _outline = [];
        _activeId = null;
        _scrollKey = null;
    }

    private async Task ShowDocumentAsync(Document document, string? path)
    {
        if (ReferenceEquals(document, _doc))
        {
            return;
        }

        _doc = document;
        _docPath = path;
        _title = document.ResolveTitle() ?? (path is null ? null : Path.GetFileNameWithoutExtension(path)) ?? "Untitled";
        _language = string.IsNullOrWhiteSpace(document.Metadata.Language) ? null : document.Metadata.Language;
        _scrollKey = ReaderSession.KeyFor(document, path);
        _restoreFraction = Session.GetScrollFraction(_scrollKey);
        _activeId = null;
        Session.AddRecent(document, path);

        if (Session.LastRender is { } cached && ReferenceEquals(cached.Document, document))
        {
            ApplyRender(cached);
            StateHasChanged();
            return;
        }

        _rendered = null;
        _renderFailed = false;
        StateHasChanged();

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;
        var resolver = ReaderImageResolver.Create(allowLocalFiles: IsDesktop);
        try
        {
            var result = await Task.Run(() =>
            {
                var html = HtmlRenderer.RenderFragment(document, new HtmlRenderOptions { HeadingAnchors = true, ResolveImage = resolver });
                ct.ThrowIfCancellationRequested();

                // Headings get their ids during the HTML render, so build the outline afterwards.
                return new ReaderRenderResult(document, html, ReaderOutlineBuilder.Build(document), document.CountWords());
            }, ct);

            if (ct.IsCancellationRequested || !ReferenceEquals(_doc, document) || _disposed)
            {
                return;
            }

            Session.LastRender = result;
            ApplyRender(result);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Rendering {Title} failed", _title);
            _renderFailed = true;
            ShowError("Couldn't display the document", $"The reading view could not be built: {ex.Message.TrimEnd('.', ' ')}. Try Pages, or open the file again.");
        }

        StateHasChanged();
    }

    private void ApplyRender(ReaderRenderResult result)
    {
        _rendered = result;
        _renderFailed = false;
        _outline = result.Outline;
        _words = result.WordCount;
    }

    /// <summary>Readable formats grouped for the start screen and the Open menu; the registry does not change at runtime.</summary>
    private IReadOnlyList<FormatGroup> Groups => _groups ??= FormatCatalog.ReadableGroups(Registry);

    private IReadOnlyList<FormatGroup>? _groups;

    private Task OpenAnyFileAsync() => OpenFileAsync(null);

    private Task OpenTypeAsync(FileFormat format) => OpenFileAsync(format);

    /// <summary>Native open dialog; <paramref name="preferred"/> puts that type's filter first, otherwise "All readable files".</summary>
    private async Task OpenFileAsync(FileFormat? preferred)
    {
        if (!Host.SupportsNativeDialogs)
        {
            return;
        }

        var filters = FormatCatalog.OpenFilters(Registry, preferred);
        var title = preferred is null ? "Open a document" : $"Open a {preferred.DisplayName} file";
        IReadOnlyList<string> picked;
        try
        {
            picked = await Host.PickFilesAsync(title, filters, multiSelect: false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Open dialog failed");
            ShowError("Couldn't open the file", "The open dialog failed: " + ex.Message);
            return;
        }

        if (picked.Count > 0)
        {
            await OpenPathAsync(picked[0]);
        }
    }

    private async Task OpenPathAsync(string path)
    {
        _error = null;
        var name = Path.GetFileName(path);
        if (!Registry.CanOpen(path))
        {
            ShowError("This format can't be read yet", UnsupportedMessage(name));
            return;
        }

        if (!File.Exists(path))
        {
            ShowError("Couldn't open the file", $"\"{path}\" does not exist or cannot be reached. Check the path and try again.");
            return;
        }

        await RunImportAsync(name, path, ct => Conversion.ImportAsync(path, null, null, ct));
    }

    private async Task OnUploadAsync(InputFileChangeEventArgs e)
    {
        var file = e.File;
        if (file is null)
        {
            return;
        }

        _error = null;
        if (!Registry.CanOpen(file.Name))
        {
            ShowError("This format can't be read yet", UnsupportedMessage(file.Name));
            return;
        }

        if (file.Size > UploadLimits.MaxDocumentBytes)
        {
            ShowError("The file is too large", $"{file.Name} is larger than {UploadLimits.Describe(UploadLimits.MaxDocumentBytes)}. Choose a smaller file.");
            return;
        }

        await RunImportAsync(file.Name, null, async ct =>
        {
            await using var upload = file.OpenReadStream(UploadLimits.MaxDocumentBytes, ct);
            using var buffer = new MemoryStream();
            await upload.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            var document = await Conversion.ImportAsync(buffer, file.Name, null, null, ct);

            // Keep the upload so "Convert this" can hand the original file to Convert (uploads have no path).
            _uploadDocument = document;
            _uploadName = file.Name;
            _uploadBytes = buffer.ToArray();
            return document;
        });
    }

    private Document? _uploadDocument;
    private string? _uploadName;
    private byte[]? _uploadBytes;

    /// <summary>
    /// Takes the open document to Convert: the file itself when it came from disk or an upload, otherwise (the sample,
    /// a publication read from the Creator) its Markdown.
    /// </summary>
    private async Task ConvertThisAsync()
    {
        if (_doc is { } document)
        {
            try
            {
                if (!string.IsNullOrEmpty(_docPath) && File.Exists(_docPath))
                {
                    Workspace.AddPath(_docPath);
                }
                else if (_uploadBytes is { Length: > 0 } bytes && _uploadName is not null && ReferenceEquals(_uploadDocument, document))
                {
                    Workspace.AddContent(_uploadName, bytes);
                }
                else if (Registry.FindExporter("markdown") is { } markdown)
                {
                    var baseName = string.Join("_", (_title ?? "document").Split(Path.GetInvalidFileNameChars())).Trim();
                    if (baseName.Length == 0)
                    {
                        baseName = "document";
                    }

                    var result = await Task.Run(() => markdown.ExportAsync(new ExportContext { Document = document, BaseName = baseName }));
                    if (result.Primary is { } primary)
                    {
                        Workspace.AddContent(primary.FileName, primary.Content);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                Logger.LogWarning(ex, "Convert this could not add {Title}", _title);
            }
        }

        Navigation.NavigateTo("/");
    }

    private Task OpenSampleAsync() =>
        RunImportAsync("the sample", null, ct => SampleDocuments.CreateWelcomeAsync(ct));

    private async Task OpenRecentAsync(ReaderRecentItem item)
    {
        _error = null;
        if (item.Path is not null)
        {
            await OpenPathAsync(item.Path);
        }
        else if (item.Document is not null)
        {
            AppState.SetDocument(item.Document, null);
            await ShowDocumentAsync(item.Document, null);
        }
    }

    private void RemoveRecent(ReaderRecentItem item) => Session.RemoveRecent(item);

    /// <summary>
    /// Back to the start page. The file stays in Recent and its scroll position is kept, so reopening it from there
    /// continues where the reader left off.
    /// </summary>
    private async Task CloseDocumentAsync()
    {
        if (_focus)
        {
            await ToggleFocusAsync();
        }

        _importCts?.Cancel();
        _loading = false;
        _error = null;
        AppState.SetDocument(null);
        ClearDocument();
    }

    private async Task RunImportAsync(string displayName, string? path, Func<CancellationToken, Task<Document>> import)
    {
        _importCts?.Cancel();
        _importCts?.Dispose();
        _importCts = new CancellationTokenSource();
        var ct = _importCts.Token;

        _loading = true;
        _loadingName = displayName;
        _error = null;
        StateHasChanged();
        try
        {
            var document = await import(ct);
            if (ct.IsCancellationRequested || _disposed)
            {
                return;
            }

            // Uploads and samples have no path on purpose (path is null): never expose a server-side location.
            AppState.SetDocument(document, path);
            await ShowDocumentAsync(document, path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (NotSupportedException ex)
        {
            ShowError("This format can't be read yet", ex.Message + " " + ReadableFormatsSentence());
        }
        catch (UnauthorizedAccessException)
        {
            ShowError("Couldn't open the file", $"AllInOne PDF does not have permission to read {displayName}. Check the file's permissions and try again.");
        }
        catch (IOException ex)
        {
            ShowError("Couldn't open the file", $"{displayName} could not be read: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Opening {File} failed", displayName);
            ShowError("Couldn't open the file", $"{displayName} could not be opened: {ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _loading = false;
            }
        }
    }

    private string UnsupportedMessage(string fileName)
    {
        var importer = Registry.FindImporter(fileName);
        var reason = importer is null
            ? $"AllInOne PDF can't read {DescribeExtension(fileName)} files."
            : importer.StatusMessage ?? $"Reading {importer.Format.DisplayName} files is not available yet.";
        return reason + " " + ReadableFormatsSentence();
    }

    private static string DescribeExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(ext) ? "these" : ext.ToLowerInvariant();
    }

    private IReadOnlyList<string> ReadableFormatNames => Registry.Importers
        .Where(i => i.Status != ConverterStatus.Planned)
        .Select(i => i.Format.DisplayName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private string ReadableFormatsSentence()
    {
        var names = ReadableFormatNames;
        return names.Count == 0 ? string.Empty : $"You can open {string.Join(", ", names)} files.";
    }

    private void ShowError(string title, string message)
    {
        _errorTitle = title;
        _error = message;
    }

    private void DismissError() => _error = null;

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.IsPathRooted(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // ---- Toolbar ------------------------------------------------------------------------------------------------

    private Task OnToolbarOpenAsync() => IsDesktop ? OpenFileAsync(null) : Task.CompletedTask; // web: the file input is anchored to the button

    private Task OnSettingsChangedAsync(ReaderViewSettings settings)
    {
        // The palette only changes through OnPaletteSelectedAsync, so the dark-mode default is never persisted by accident.
        UpdateSettings(settings with { Palette = _settings.Palette });
        return Task.CompletedTask;
    }

    private Task OnPaletteSelectedAsync(ReaderTheme palette)
    {
        var wasChosen = _paletteChosen;
        _paletteChosen = true;
        if (!wasChosen && palette == _settings.Palette)
        {
            // Same palette, but now an explicit choice for this session.
            Session.Adjust(_settings, _paletteChosen);
            StateHasChanged();
            return Task.CompletedTask;
        }

        UpdateSettings(_settings with { Palette = palette });
        return Task.CompletedTask;
    }

    private Task ToggleOutlineAsync()
    {
        UpdateSettings(_settings with { ShowOutline = !_settings.ShowOutline });
        return Task.CompletedTask;
    }

    private async Task ToggleFocusAsync()
    {
        _focus = !_focus;
        StateHasChanged();
        try
        {
            if (_handle is not null)
            {
                await _handle.InvokeVoidAsync("setFocusMode", _focus);
            }
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private Task SetModeAsync(ReaderMode mode)
    {
        if (mode == _mode)
        {
            return Task.CompletedTask;
        }

        _mode = mode;
        Session.Mode = mode;
        if (mode == ReaderMode.Reflow && _scrollKey is not null)
        {
            _restoreFraction = Session.GetScrollFraction(_scrollKey);
        }

        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// An adjustment made while reading. It lasts for this session only (<see cref="ReaderSession.Adjust"/>); the
    /// reading defaults in Settings are never overwritten from here.
    /// </summary>
    private void UpdateSettings(ReaderViewSettings settings)
    {
        settings = settings.Normalized();
        if (settings == _settings)
        {
            return;
        }

        _settings = settings;
        Session.Adjust(settings, _paletteChosen);
        StateHasChanged();
    }

    /// <summary>The user's reading defaults from Settings (what "Reset to defaults" in the Text panel restores).</summary>
    private ReaderViewSettings Defaults => ReaderViewSettings.FromPreferences(Preferences.Current);

    private void OnPreferencesChanged()
    {
        _ = InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            // New defaults from Settings (or a reset there) replace this session's adjustments; other preference
            // changes (theme, Convert options…) leave them alone.
            var latest = ReaderViewSettings.FromPreferences(Preferences.Current);
            var chosen = Preferences.Current.ReaderPaletteChosen;
            if (latest == _lastDefaults && chosen == _lastPaletteChosenDefault)
            {
                StateHasChanged();
                return;
            }

            _lastDefaults = latest;
            _lastPaletteChosenDefault = chosen;
            Session.ClearAdjustments();
            _settings = latest;
            _paletteChosen = chosen;
            StateHasChanged();
        });
    }
    private void OnThemeChanged() => _ = InvokeAsync(() =>
    {
        if (!_disposed)
        {
            StateHasChanged();
        }
    });

    // ---- Surface callbacks ---------------------------------------------------------------------------------------

    private void OnScrollFraction(double fraction)
    {
        if (_scrollKey is not null)
        {
            Session.SetScrollFraction(_scrollKey, fraction);
        }
    }

    private void OnActiveHeading(string? id) => _activeId = id;

    private async Task NavigateToHeadingAsync(string id)
    {
        _activeId = id;
        if (_surface is not null)
        {
            await _surface.ScrollToIdAsync(id);
        }
    }

    // ---- JS callbacks ----------------------------------------------------------------------------------------------

    [JSInvokable]
    public Task OnReaderCommand(string command) => InvokeAsync(async () =>
    {
        if (_disposed || _doc is null)
        {
            return;
        }

        switch (command)
        {
            case "bigger":
                if (_mode == ReaderMode.Pages)
                {
                    if (_pages is not null)
                    {
                        await _pages.ZoomInAsync();
                    }
                }
                else
                {
                    UpdateSettings(_settings.WithFontSizeStep(+1));
                }

                break;
            case "smaller":
                if (_mode == ReaderMode.Pages)
                {
                    if (_pages is not null)
                    {
                        await _pages.ZoomOutAsync();
                    }
                }
                else
                {
                    UpdateSettings(_settings.WithFontSizeStep(-1));
                }

                break;
            case "outline":
                if (_mode == ReaderMode.Reflow)
                {
                    await ToggleOutlineAsync();
                }

                break;
            case "focus":
                await ToggleFocusAsync();
                break;
            case "escape":
                if (_toolbar?.ClosePanel() == true)
                {
                    break;
                }

                if (_focus)
                {
                    await ToggleFocusAsync();
                }

                break;
            case "reflow":
                await SetModeAsync(ReaderMode.Reflow);
                break;
            case "pages":
                await SetModeAsync(ReaderMode.Pages);
                break;
        }
    });
}
