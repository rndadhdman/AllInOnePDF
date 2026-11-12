using AllInOnePdf.Core.Fonts;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.UI.Components.Shell;
using AllInOnePdf.UI.Services;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace AllInOnePdf.UI.Pages;

public sealed partial class Settings
{
    private const string DefaultAccentHex = ThemeService.DefaultAccent;
    private static readonly TimeSpan SliderDebounce = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan SavedIndicatorTime = TimeSpan.FromSeconds(2.5);

    private enum SaveState { Idle, Saving, Saved, Failed }

    private sealed record Section(string Id, string Title, Icon Icon);
    private sealed record ModeChoice(AppThemeMode Mode, string Label, Icon Icon);
    private sealed record PaletteChoice(ReaderTheme Theme, string Label, string CssClass);

    private static readonly Section[] Sections =
    [
        new("appearance", "Appearance", new Icons.Regular.Size20.DarkTheme()),
        new("reading", "Reading", new Icons.Regular.Size20.BookOpen()),
        new("output", "Output", new Icons.Regular.Size20.Folder()),
        new("behaviour", "Behaviour", new Icons.Regular.Size20.Options()),
        new("about", "About", new Icons.Regular.Size20.Info()),
    ];

    private static readonly ModeChoice[] ThemeModes =
    [
        new(AppThemeMode.System, "System", new Icons.Regular.Size20.Desktop()),
        new(AppThemeMode.Light, "Light", new Icons.Regular.Size20.WeatherSunny()),
        new(AppThemeMode.Dark, "Dark", new Icons.Regular.Size20.WeatherMoon()),
    ];

    private static readonly PaletteChoice[] Palettes =
    [
        new(ReaderTheme.Paper, "Paper", "aio-reader-paper"),
        new(ReaderTheme.Sepia, "Sepia", "aio-reader-sepia"),
        new(ReaderTheme.Dusk, "Dusk", "aio-reader-dusk"),
        new(ReaderTheme.Night, "Night", "aio-reader-night"),
    ];

    [Inject] private IPreferencesService Preferences { get; set; } = default!;
    [Inject] private IThemeStore ThemeStore { get; set; } = default!;
    [Inject] private IHostServices Host { get; set; } = default!;
    [Inject] private AppPaths Paths { get; set; } = default!;
    [Inject] private IDialogService Dialogs { get; set; } = default!;
    [Inject] private IToastService Toasts { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ThemeService Theme { get; set; } = default!;

    private readonly Debouncer _sliderDebounce = new(SliderDebounce);
    private UserPreferences _baseline = new();
    private UserPreferences _draft = new();
    private IReadOnlyList<PdfTheme> _themes = [];
    private string? _defaultThemeId;
    private string? _hexInput;
    private string? _hexError;
    private string _activeSection = "appearance";
    private SaveState _saveState;
    private int _saveGeneration;
    private bool _disposed;
    private readonly string _spyKey = $"settings-{Guid.NewGuid():N}";
    private DotNetObjectReference<Settings>? _selfRef;

    /// <summary>The app's own title bar exists only in the Windows desktop app; elsewhere the switch would do nothing.</summary>
    private bool ShowTitleBarSetting => Host.Kind == HostKind.Desktop && OperatingSystem.IsWindows();

    private bool IsCustomAccent => _draft.AccentColor is not null && ThemeService.Swatches.All(s => !string.Equals(s.Hex, _draft.AccentColor, StringComparison.OrdinalIgnoreCase));

    private string SaveStatusClass => _saveState switch
    {
        SaveState.Saving => "is-saving",
        SaveState.Saved => "is-saved",
        SaveState.Failed => "is-failed",
        _ => "is-idle",
    };

    /// <summary>The same --aio-doc-* variables the Reader surface sets, so the sample renders like the Reader.</summary>
    private string ReaderPreviewStyle => ReaderViewSettings.FromPreferences(_draft).ToCssVariables();

    private string SampleFontStyle => "font-family:" + ReaderViewSettings.FontStack(ReaderViewSettings.FromPreferences(_draft).Font);

    private string PreviewPaletteClass => ReaderViewSettings.PaletteClass(
        ReaderViewSettings.EffectivePalette(_draft.ReaderTheme, _draft.ReaderPaletteChosen, Theme.IsDark));

    protected override async Task OnInitializedAsync()
    {
        Preferences.Changed += OnPreferencesChanged;
        ThemeStore.Changed += OnThemesChanged;
        Theme.Changed += OnAppThemeChanged;
        await Preferences.LoadAsync();
        SyncFromPreferences();
        await LoadThemesAsync();
    }

    private void SyncFromPreferences()
    {
        _baseline = Preferences.Current.Clone();
        _draft = Preferences.Current.Clone();
        _hexInput = _draft.AccentColor;
        _hexError = null;
    }

    private async Task LoadThemesAsync()
    {
        try
        {
            _themes = await ThemeStore.GetAllAsync();
            _defaultThemeId = await ThemeStore.GetDefaultThemeIdAsync();
        }
        catch (IOException)
        {
            _themes = [];
        }
    }

    // ---- Saving ------------------------------------------------------------

    /// <summary>Persists whatever differs between the draft and the last known preferences.</summary>
    private async Task CommitAsync()
    {
        CancelDebounce();
        var before = _baseline;
        var after = _draft.Clone();
        var generation = ++_saveGeneration;
        _saveState = SaveState.Saving;
        try
        {
            await Preferences.UpdateAsync(p => p.ApplyChanges(before, after));
            _baseline = after;
            _saveState = SaveState.Saved;
            _ = HideSavedLaterAsync(generation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saveState = SaveState.Failed;
            Toasts.ShowError($"Settings could not be saved: {ex.Message} Check that the data folder is writable.");
        }
    }

    /// <summary>Sliders fire often; save shortly after the last change.</summary>
    private void QueueCommit() => _sliderDebounce.Debounce(_ => InvokeAsync(async () =>
    {
        if (_disposed)
        {
            return;
        }

        await CommitAsync();
        StateHasChanged();
    }));

    private void CancelDebounce() => _sliderDebounce.Cancel();

    private async Task HideSavedLaterAsync(int generation)
    {
        await Task.Delay(SavedIndicatorTime);
        if (_disposed || generation != _saveGeneration || _saveState != SaveState.Saved)
        {
            return;
        }

        _saveState = SaveState.Idle;
        await InvokeAsync(StateHasChanged);
    }

    private void OnPreferencesChanged()
    {
        _ = InvokeAsync(() =>
        {
            // Keep in-progress slider edits; otherwise follow changes from the header or another window.
            if (_disposed || _sliderDebounce.IsPending || _saveState == SaveState.Saving)
            {
                return;
            }

            SyncFromPreferences();
            StateHasChanged();
        });
    }

    private void OnThemesChanged()
    {
        _ = InvokeAsync(async () =>
        {
            if (_disposed)
            {
                return;
            }

            await LoadThemesAsync();
            StateHasChanged();
        });
    }

    // The Automatic palette preview follows the app's light/dark mode.
    private void OnAppThemeChanged() => _ = InvokeAsync(() =>
    {
        if (!_disposed)
        {
            StateHasChanged();
        }
    });

    // ---- Appearance --------------------------------------------------------

    private Task SetThemeModeAsync(AppThemeMode mode)
    {
        _draft.ThemeMode = mode;
        return CommitAsync();
    }

    private Task SetAccentAsync(string? hex)
    {
        _draft.AccentColor = HexColor.Normalize(hex);
        _hexInput = _draft.AccentColor;
        _hexError = null;
        return CommitAsync();
    }

    private Task OnColorPicked(ChangeEventArgs e) => SetAccentAsync(e.Value as string);

    private Task OnHexEnteredAsync()
    {
        if (string.IsNullOrWhiteSpace(_hexInput))
        {
            return SetAccentAsync(null);
        }

        var normalized = HexColor.Normalize(_hexInput);
        if (normalized is null)
        {
            _hexError = "Enter a colour like #0F6CBD.";
            return Task.CompletedTask;
        }

        return SetAccentAsync(normalized);
    }

    private static string SwatchStyle(string? hex) => $"--aio-swatch: {hex ?? "var(--aio-default-accent)"};";

    // ---- Reading -----------------------------------------------------------

    /// <summary>An explicit palette: the Reader uses it in both light and dark mode.</summary>
    private Task SetPaletteAsync(ReaderTheme theme)
    {
        _draft.ReaderTheme = theme;
        _draft.ReaderPaletteChosen = true;
        return CommitAsync();
    }

    /// <summary>Automatic: Paper in light mode, Dusk in dark mode.</summary>
    private Task SetAutomaticPaletteAsync()
    {
        _draft.ReaderTheme = ReaderTheme.Paper;
        _draft.ReaderPaletteChosen = false;
        return CommitAsync();
    }

    // ---- Output ------------------------------------------------------------

    private async Task ChooseFolderAsync()
    {
        var folder = await Host.PickFolderAsync("Choose output folder", _draft.DefaultOutputDirectory);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _draft.DefaultOutputDirectory = folder;
        await CommitAsync();
    }

    private static string ThemeLabel(PdfTheme theme) => theme.IsBuiltIn ? $"{theme.Name} (built-in)" : theme.Name;

    private async Task OnDefaultThemeChangedAsync()
    {
        if (string.IsNullOrEmpty(_defaultThemeId))
        {
            return;
        }

        // The theme store is the only source of truth for the default theme (IThemeStore.Changed refreshes this page).
        try
        {
            await ThemeStore.SetDefaultThemeIdAsync(_defaultThemeId);
            _saveState = SaveState.Saved;
            _ = HideSavedLaterAsync(++_saveGeneration);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toasts.ShowError($"The default theme could not be saved: {ex.Message}");
        }
    }

    private Task SetParallelAsync(int value)
    {
        _draft.MaxParallelConversions = Math.Clamp(value, 1, 4);
        return CommitAsync();
    }

    private Task SetOverwritePolicyAsync(string? value)
    {
        if (!Enum.TryParse<OverwritePolicy>(value, out var policy) || policy == _draft.OverwritePolicy)
        {
            return Task.CompletedTask;
        }

        _draft.OverwritePolicy = policy;
        return CommitAsync();
    }

    // ---- About -------------------------------------------------------------

    private async Task OpenDataFolderAsync()
    {
        try
        {
            Paths.EnsureCreated();
            await Host.RevealInFolderAsync(Paths.DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toasts.ShowError($"The data folder could not be opened: {ex.Message}");
        }
    }

    private async Task CopyDataPathAsync()
    {
        await Host.CopyToClipboardAsync(Paths.DataDirectory);
        Toasts.ShowInfo("Data folder path copied");
    }

    private Task OpenRepositoryAsync() => Host.OpenUrlAsync(BuildInfo.RepositoryUrl);

    private Task OpenReleasesAsync() => Host.OpenUrlAsync(BuildInfo.ReleasesUrl);

    private Task OpenWebsiteAsync() => Host.OpenUrlAsync(BuildInfo.WebsiteUrl);

    private async Task CopyDiagnosticsAsync()
    {
        string? themeId = _defaultThemeId;
        if (themeId is null)
        {
            try
            {
                themeId = await ThemeStore.GetDefaultThemeIdAsync();
            }
            catch (IOException)
            {
            }
        }

        var report = DiagnosticsReport.Build(Host.Kind, Paths.DataDirectory, Paths.IsPortable, themeId);

        // Both hosts copy through aio.copyText (the webview clipboard); call it directly to learn whether it worked.
        bool copied;
        try
        {
            copied = await JS.InvokeAsync<bool>("aio.copyText", report);
        }
        catch (JSException)
        {
            copied = false;
        }

        if (copied)
        {
            Toasts.ShowSuccess("Diagnostics copied. Paste them into your problem report.");
            return;
        }

        // No clipboard access: show the text so it can be selected and copied by hand.
        await ShowMarkupAsync(
            "Diagnostics",
            new MarkupDialogContent(
                "<pre><code>" + System.Net.WebUtility.HtmlEncode(report) + "</code></pre>",
                "Diagnostics",
                "The clipboard is not available here. Select the text below, copy it and paste it into your problem report."));
    }

    private async Task ShowNoticesAsync()
    {
        var html = ThirdPartyNotices.Html;
        if (html is null)
        {
            Toasts.ShowError("The notices are missing from this build. Read THIRD-PARTY-NOTICES.md in the source repository instead.");
            return;
        }

        await ShowMarkupAsync("Third-party notices", new MarkupDialogContent(html, "Third-party notices"));
    }

    private async Task ShowMarkupAsync(string title, MarkupDialogContent content)
    {
        await Dialogs.ShowDialogAsync<MarkupDialog>(content, new DialogParameters
        {
            Title = title,
            PrimaryAction = "Close",
            SecondaryAction = null,
            Width = "880px",
            TrapFocus = true,
            Modal = true,
        });
    }

    // ---- Page --------------------------------------------------------------

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        // The section list follows scrolling (S1): JS reports the section at the top of the view.
        try
        {
            _selfRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("aio.watchSections", _selfRef, _spyKey, Sections.Select(s => s.Id).ToArray());
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
        }
    }

    [JSInvokable]
    public Task OnSectionVisible(string id) => InvokeAsync(() =>
    {
        if (_disposed || _activeSection == id || Sections.All(s => s.Id != id))
        {
            return;
        }

        _activeSection = id;
        StateHasChanged();
    });

    private async Task JumpToAsync(string id)
    {
        _activeSection = id;
        try
        {
            await JS.InvokeAsync<bool>("aio.jumpToSection", _spyKey, id);
        }
        catch (JSException)
        {
        }
    }

    private async Task ResetAsync()
    {
        if (!await Dialogs.ConfirmDestructiveAsync(
                "All settings on this page go back to their defaults. Your themes, history and files are not affected.",
                "Reset",
                "Reset settings?"))
        {
            return;
        }

        CancelDebounce();
        try
        {
            await Preferences.ResetAsync(Paths.DefaultOutputDirectory);
            await ThemeStore.SetDefaultThemeIdAsync(BuiltInThemes.DefaultId);
            _defaultThemeId = BuiltInThemes.DefaultId;
            SyncFromPreferences();
            Toasts.ShowSuccess("Settings reset to defaults");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toasts.ShowError($"Settings could not be reset: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Preferences.Changed -= OnPreferencesChanged;
        ThemeStore.Changed -= OnThemesChanged;
        Theme.Changed -= OnAppThemeChanged;
        _sliderDebounce.Dispose();
        try
        {
            await JS.InvokeVoidAsync("aio.unwatchSections", _spyKey);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
        }

        _selfRef?.Dispose();
    }
}
