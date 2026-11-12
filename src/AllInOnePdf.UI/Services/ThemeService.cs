using AllInOnePdf.UI.Services.Shared;
using Microsoft.FluentUI.AspNetCore.Components;

namespace AllInOnePdf.UI.Services;

/// <summary>An accent colour offered in Settings.</summary>
public sealed record AccentSwatch(string Name, string? Hex);

/// <summary>
/// Bridges <see cref="UserPreferences"/> (theme mode, accent, density, nav state) with <see cref="FluentDesignTheme"/>
/// in the shell. Scoped: one per circuit / window. Raises <see cref="Changed"/> whenever anything visual changes,
/// including the OS colour scheme while the mode is System (so <see cref="IsDark"/> is always the effective value;
/// the Reader can use it to pick Dusk in dark mode).
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly IPreferencesService _preferences;
    private readonly GlobalState _globalState;
    private bool _isDark;
    private bool _systemReported;

    public ThemeService(IPreferencesService preferences, GlobalState globalState)
    {
        _preferences = preferences;
        _globalState = globalState;
        _preferences.Changed += OnPreferencesChanged;
    }

    /// <summary>Raised after preferences or the effective luminance change. May fire off the renderer thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Accent palette: Fluent default first, then the Office colour set (the same hex values as Fluent's
    /// <c>OfficeColor</c> list), named by colour rather than by product.
    /// </summary>
    public static IReadOnlyList<AccentSwatch> Swatches { get; } =
    [
        new("Default blue", null),
        new("Lavender", "#6264A7"),
        new("Cobalt", "#2B579A"),
        new("Royal blue", "#3955A3"),
        new("Violet", "#7719AA"),
        new("Plum", "#742774"),
        new("Raspberry", "#BC1948"),
        new("Brick red", "#A4373A"),
        new("Rust", "#B7472A"),
        new("Orange", "#D83B01"),
        new("Green", "#217346"),
        new("Leaf green", "#31752F"),
        new("Teal", "#008272"),
        new("Pine", "#077568"),
        new("Turquoise", "#00A2AD"),
    ];

    public UserPreferences Preferences => _preferences.Current;

    public AppThemeMode ThemeMode => _preferences.Current.ThemeMode;

    public DesignThemeModes Mode => _preferences.Current.ThemeMode switch
    {
        AppThemeMode.Light => DesignThemeModes.Light,
        AppThemeMode.Dark => DesignThemeModes.Dark,
        _ => DesignThemeModes.System,
    };

    /// <summary>Fluent 2 brand blue (DESIGN.md); Fluent derives the lighter dark-mode variant itself.</summary>
    public const string DefaultAccent = "#0F6CBD";

    /// <summary>Normalised user accent (#RRGGBB) or null when the default is used.</summary>
    public string? AccentColor => HexColor.Normalize(_preferences.Current.AccentColor);

    /// <summary>
    /// Accent passed to <see cref="FluentDesignTheme.CustomColor"/>. Always set (default blue when the user has no
    /// accent) because FluentDesignTheme does not revert a custom colour when switching back to an OfficeColor.
    /// </summary>
    public string CustomColor => AccentColor ?? DefaultAccent;

    public bool Compact => _preferences.Current.CompactDensity;

    public bool NavCollapsed => _preferences.Current.NavCollapsed;

    /// <summary>Effective luminance: explicit Light/Dark, or the OS colour scheme when the mode is System.</summary>
    public bool IsDark => ThemeMode switch
    {
        AppThemeMode.Dark => true,
        AppThemeMode.Light => false,
        _ => _isDark,
    };

    /// <summary>Loads preferences once. Safe to call from every component that needs them.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => _preferences.LoadAsync(cancellationToken);

    /// <summary>
    /// Pushes the effective mode into Fluent's <see cref="GlobalState"/>. Components that pick colours in C# (message
    /// bars, badges) read it; without it they keep light-mode colours on dark layers. Call on the renderer thread
    /// (the shell does, on start-up and after every <see cref="Changed"/>).
    /// </summary>
    public void SyncLuminance()
    {
        var luminance = IsDark ? StandardLuminance.DarkMode : StandardLuminance.LightMode;        if (_globalState.Luminance != luminance)
        {
            _globalState.SetLuminance(luminance);
        }
    }

    public Task SetModeAsync(AppThemeMode mode) =>
        ThemeMode == mode ? Task.CompletedTask : _preferences.UpdateAsync(p => p.ThemeMode = mode);

    public Task SetAccentAsync(string? hex)
    {
        var normalized = HexColor.Normalize(hex);
        return normalized == AccentColor ? Task.CompletedTask : _preferences.UpdateAsync(p => p.AccentColor = normalized);
    }

    public Task SetCompactAsync(bool compact) =>
        Compact == compact ? Task.CompletedTask : _preferences.UpdateAsync(p => p.CompactDensity = compact);

    public Task SetNavCollapsedAsync(bool collapsed) =>
        NavCollapsed == collapsed ? Task.CompletedTask : _preferences.UpdateAsync(p => p.NavCollapsed = collapsed);

    /// <summary>
    /// False only while the mode is System and the shell has not reported the OS colour scheme yet (first render),
    /// so surfaces that differ by luminance (the Reader palette) can avoid a light flash.
    /// </summary>
    public bool IsDarkKnown => ThemeMode != AppThemeMode.System || _systemReported;

    /// <summary>Called by the shell with the OS colour scheme (prefers-color-scheme: dark).</summary>
    public void ReportSystemDark(bool isDark)
    {
        var firstReport = !_systemReported;
        _systemReported = true;
        if (_isDark == isDark && !firstReport)
        {
            return;
        }

        _isDark = isDark;
        Changed?.Invoke();
    }

    public void Dispose() => _preferences.Changed -= OnPreferencesChanged;

    private void OnPreferencesChanged() => Changed?.Invoke();
}
