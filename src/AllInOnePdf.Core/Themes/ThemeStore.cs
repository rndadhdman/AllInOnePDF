using System.Text.Json;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AllInOnePdf.Core.Themes;

/// <summary>Persists user themes and exposes built-in ones. User themes live as JSON files in the app data folder.</summary>
public interface IThemeStore
{
    /// <summary>Built-in themes first, then user themes sorted by name.</summary>
    Task<IReadOnlyList<PdfTheme>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<PdfTheme?> GetAsync(string? id, CancellationToken cancellationToken = default);

    Task<PdfTheme> GetDefaultAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves a user theme (built-in ids are rejected; duplicate them first).</summary>
    Task SaveAsync(PdfTheme theme, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<PdfTheme> DuplicateAsync(string id, string? newName = null, CancellationToken cancellationToken = default);

    /// <summary>Imports a theme JSON (exported from this app). Assigns a fresh id if one already exists.</summary>
    Task<PdfTheme> ImportAsync(string json, CancellationToken cancellationToken = default);

    /// <summary>Id of the theme used when none is chosen; persisted in preferences.</summary>
    Task<string> GetDefaultThemeIdAsync(CancellationToken cancellationToken = default);

    Task SetDefaultThemeIdAsync(string id, CancellationToken cancellationToken = default);

    event Action? Changed;
}

public sealed class FileThemeStore : IThemeStore, IDisposable
{
    private readonly string _directory;
    private readonly JsonFileStore _settings;
    private readonly ILogger<FileThemeStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose()
    {
        _gate.Dispose();
        _settings.Dispose();
    }

    private const string DefaultKey = "defaultThemeId";

    public FileThemeStore(AppPaths paths, ILogger<FileThemeStore> logger)
    {
        _directory = paths.ThemesDirectory;
        _settings = new JsonFileStore(Path.Combine(paths.DataDirectory, "theme-settings.json"));
        _logger = logger;
    }

    public event Action? Changed;

    public async Task<IReadOnlyList<PdfTheme>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<PdfTheme>(BuiltInThemes.All());
        var users = new List<PdfTheme>();
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var json = await File.ReadAllTextAsync(file, cancellationToken);
                    var theme = JsonSerializer.Deserialize<PdfTheme>(json, CoreJson.Options);
                    if (theme is not null)
                    {
                        ThemeValidator.Normalize(theme);
                        theme.IsBuiltIn = false;
                        users.Add(theme);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Skipping unreadable theme file {File}", file);
                }
            }
        }

        list.AddRange(users.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase));
        return list;
    }

    public async Task<PdfTheme?> GetAsync(string? id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var builtIn = BuiltInThemes.Find(id);
        if (builtIn is not null)
        {
            return builtIn;
        }

        var file = PathFor(id);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(file, cancellationToken);
            var theme = JsonSerializer.Deserialize<PdfTheme>(json, CoreJson.Options);
            if (theme is not null)
            {
                ThemeValidator.Normalize(theme);
                theme.IsBuiltIn = false;
            }

            return theme;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Theme file {File} could not be read", file);
            return null;
        }
    }

    public async Task<PdfTheme> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        var id = await GetDefaultThemeIdAsync(cancellationToken);
        return await GetAsync(id, cancellationToken) ?? BuiltInThemes.Clean();
    }

    public async Task SaveAsync(PdfTheme theme, CancellationToken cancellationToken = default)
    {
        if (BuiltInThemes.Find(theme.Id) is not null)
        {
            throw new InvalidOperationException("Built-in themes are read-only. Duplicate the theme to edit it.");
        }

        ThemeValidator.Normalize(theme);
        theme.IsBuiltIn = false;
        theme.Modified = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            var tmp = PathFor(theme.Id) + ".tmp";
            await File.WriteAllTextAsync(tmp, theme.ToJson(), cancellationToken);
            File.Move(tmp, PathFor(theme.Id), overwrite: true);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (BuiltInThemes.Find(id) is not null)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var file = PathFor(id);
            if (!File.Exists(file))
            {
                return false;
            }

            File.Delete(file);
        }
        finally
        {
            _gate.Release();
        }

        if (await GetDefaultThemeIdAsync(cancellationToken) == id)
        {
            await SetDefaultThemeIdAsync(BuiltInThemes.DefaultId, cancellationToken);
        }

        Changed?.Invoke();
        return true;
    }

    public async Task<PdfTheme> DuplicateAsync(string id, string? newName = null, CancellationToken cancellationToken = default)
    {
        var source = await GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"Theme '{id}' not found.");
        var copy = source.Duplicate(newName);
        await SaveAsync(copy, cancellationToken);
        return copy;
    }

    /// <summary>Imports theme JSON. Malformed input throws <see cref="JsonException"/>; odd values are repaired by <see cref="ThemeValidator"/>.</summary>
    public async Task<PdfTheme> ImportAsync(string json, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 2 * 1024 * 1024)
        {
            throw new JsonException("Theme JSON is empty or larger than 2 MB.");
        }

        var theme = ThemeValidator.Normalize(PdfTheme.FromJson(json));
        if (BuiltInThemes.Find(theme.Id) is not null || File.Exists(PathFor(theme.Id)))
        {
            theme.Id = Guid.NewGuid().ToString("N");
        }

        await SaveAsync(theme, cancellationToken);
        return theme;
    }

    public async Task<string> GetDefaultThemeIdAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.ReadAsync<Dictionary<string, string>>(cancellationToken) ?? [];
        return settings.GetValueOrDefault(DefaultKey) ?? BuiltInThemes.DefaultId;
    }

    public async Task SetDefaultThemeIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var settings = await _settings.ReadAsync<Dictionary<string, string>>(cancellationToken) ?? [];
        settings[DefaultKey] = id;
        await _settings.WriteAsync(settings, cancellationToken);
        Changed?.Invoke();
    }

    private string PathFor(string id)
    {
        var safe = ConversionService.SanitizeBaseName(id);
        return Path.Combine(_directory, safe + ".json");
    }
}
