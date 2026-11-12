using System.Reflection;
using AllInOnePdf.Core.Storage;

namespace AllInOnePdf.UI.Services;

public enum AppThemeMode { System, Light, Dark }

public enum ReaderTheme { Paper, Sepia, Dusk, Night }

/// <summary>User preferences persisted as JSON in the app data folder.</summary>
public sealed class UserPreferences
{
    // Appearance
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;
    /// <summary>Accent colour hex, null = Fluent default (Intune blue).</summary>
    public string? AccentColor { get; set; }
    public bool CompactDensity { get; set; }
    /// <summary>Left navigation shown as an icon rail.</summary>
    public bool NavCollapsed { get; set; }
    /// <summary>
    /// Desktop: show the operating system's window frame instead of the app's own title bar (caption buttons in the
    /// header). Off by default.
    /// </summary>
    public bool UseSystemTitleBar { get; set; }

    // Reading
    public ReaderTheme ReaderTheme { get; set; } = ReaderTheme.Paper;
    /// <summary>
    /// True once the user picked a palette (Reader toolbar or Settings). Until then the Reader follows the app:
    /// Paper in light mode, Dusk in dark mode ("Automatic" in Settings).
    /// </summary>
    public bool ReaderPaletteChosen { get; set; }
    public string ReaderFont { get; set; } = "Literata";
    public double ReaderFontSizePx { get; set; } = 19;
    public double ReaderLineHeight { get; set; } = 1.7;
    /// <summary>Column width in characters (measure). 60–75 is comfortable.</summary>
    public int ReaderMeasureCh { get; set; } = 68;
    public bool ReaderJustify { get; set; }
    public bool ReaderShowOutline { get; set; } = true;

    // Output
    public string? DefaultOutputDirectory { get; set; }
    public bool SaveNextToSource { get; set; } = true;
    public bool OpenAfterConvert { get; set; }
    public bool RevealAfterConvert { get; set; } = true;
    /// <summary>Conversions that run at once (applied to <c>ConversionQueue.MaxParallel</c>).</summary>
    public int MaxParallelConversions { get; set; } = 2;
    /// <summary>What Convert does when an output file already exists.</summary>
    public OverwritePolicy OverwritePolicy { get; set; } = OverwritePolicy.Ask;

    // The default PDF theme is not a preference: IThemeStore.GetDefaultThemeIdAsync is its only source of truth.

    // Behaviour
    public bool KeepHistory { get; set; } = true;
    public bool ShowWelcomeSample { get; set; } = true;

    private static readonly PropertyInfo[] CopyableProperties = typeof(UserPreferences)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite)
        .ToArray();

    /// <summary>Shallow copy (all properties are values or strings).</summary>
    public UserPreferences Clone()
    {
        var copy = new UserPreferences();
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Copies every property from <paramref name="other"/> into this instance.</summary>
    public void CopyFrom(UserPreferences other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var property in CopyableProperties)
        {
            property.SetValue(this, property.GetValue(other));
        }
    }

    /// <summary>
    /// Applies only the properties that differ between <paramref name="before"/> and <paramref name="after"/>, so an
    /// editor working on a snapshot does not overwrite unrelated changes made elsewhere in the meantime.
    /// </summary>
    /// <returns>True when anything changed.</returns>
    public bool ApplyChanges(UserPreferences before, UserPreferences after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changed = false;
        foreach (var property in CopyableProperties)
        {
            var value = property.GetValue(after);
            if (!Equals(property.GetValue(before), value))
            {
                property.SetValue(this, value);
                changed = true;
            }
        }

        return changed;
    }
}

public static class PreferencesServiceExtensions
{
    /// <summary>Restores every preference to its default and persists. The output folder falls back to <paramref name="defaultOutputDirectory"/>.</summary>
    public static Task ResetAsync(this IPreferencesService service, string? defaultOutputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        var defaults = new UserPreferences { DefaultOutputDirectory = defaultOutputDirectory };
        return service.UpdateAsync(p => p.CopyFrom(defaults), cancellationToken);
    }
}

public interface IPreferencesService
{
    UserPreferences Current { get; }
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    /// <summary>Mutates and persists in one step.</summary>
    Task UpdateAsync(Action<UserPreferences> update, CancellationToken cancellationToken = default);
    event Action? Changed;
}

public sealed class FilePreferencesService : IPreferencesService, IDisposable
{
    private readonly JsonFileStore _store;
    private readonly AppPaths _paths;
    private readonly Lock _loadLock = new();
    private Task? _loadTask;
    private volatile bool _loaded;

    public FilePreferencesService(AppPaths paths)
    {
        _paths = paths;
        _store = new JsonFileStore(paths.PreferencesFile);
    }

    public UserPreferences Current { get; private set; } = new();

    public event Action? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
        {
            return Task.CompletedTask;
        }

        // Several components may ask at once (layout, settings page); share one read.
        lock (_loadLock)
        {
            _loadTask ??= LoadCoreAsync();
            return _loadTask.WaitAsync(cancellationToken);
        }
    }

    private async Task LoadCoreAsync()
    {
        var loaded = await _store.ReadAsync<UserPreferences>(CancellationToken.None) ?? new UserPreferences();
        loaded.DefaultOutputDirectory ??= _paths.DefaultOutputDirectory;
        Current = loaded;
        _loaded = true;
        Changed?.Invoke();
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _store.WriteAsync(Current, cancellationToken);
        Changed?.Invoke();
    }

    public async Task UpdateAsync(Action<UserPreferences> update, CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken);
        update(Current);
        await SaveAsync(cancellationToken);
    }

    public void Dispose() => _store.Dispose();
}
