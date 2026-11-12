using System.Text.Json;
using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.Core.Storage;

/// <summary>
/// Where the app keeps user data. Resolution order: the <c>ALLINONEPDF_DATA_DIR</c> variable, then portable mode
/// (a <c>portable.txt</c> marker next to the executable, or a PortableApps.com <c>App/AllInOnePdf</c> layout), then
/// %APPDATA% on Windows / XDG config on Linux.
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "AllInOnePdf";

    /// <summary>Environment variable that overrides the data folder (tests, reviews, containers).</summary>
    public const string DataDirectoryVariable = "ALLINONEPDF_DATA_DIR";

    /// <summary>
    /// Marker file next to the executable that switches the app to portable mode. Empty = data in a <c>Data</c>
    /// folder beside the executable; otherwise its first non-empty line is a path relative to the executable.
    /// </summary>
    public const string PortableMarkerFileName = "portable.txt";

    public AppPaths()
        : this(ResolveDataRoot(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(DataDirectoryVariable), out var portable), portable)
    {
    }

    public AppPaths(string dataDirectory)
        : this(dataDirectory, isPortable: false)
    {
    }

    public AppPaths(string dataDirectory, bool isPortable)
    {
        DataDirectory = dataDirectory;
        IsPortable = isPortable;
    }

    /// <summary>Root for themes, history and preferences.</summary>
    public string DataDirectory { get; }

    /// <summary>True when the data folder lives beside the executable (USB stick / PortableApps.com style).</summary>
    public bool IsPortable { get; }

    /// <summary>Browser (WebView2 / WebKit) profile folder: kept with the data so a portable install leaves no trace elsewhere.</summary>
    public string BrowserDataDirectory => Path.Combine(DataDirectory, "browser");

    public string ThemesDirectory => Path.Combine(DataDirectory, "themes");

    /// <summary>Creator publications (<c>.aiopub</c> packages).</summary>
    public string PublicationsDirectory => Path.Combine(DataDirectory, "publications");

    /// <summary>Autosaved editor drafts for crash recovery (see <see cref="IDraftStore"/>).</summary>
    public string DraftsDirectory => Path.Combine(DataDirectory, "drafts");

    public string HistoryFile =>Path.Combine(DataDirectory, "history.json");

    public string PreferencesFile => Path.Combine(DataDirectory, "preferences.json");

    public string CacheDirectory => Path.Combine(DataDirectory, "cache");

    /// <summary>Default output folder: an "Output" folder in the portable data folder, or "AllInOne PDF" in the user's Documents.</summary>
    public string DefaultOutputDirectory
    {
        get
        {
            if (IsPortable)
            {
                return Path.Combine(DataDirectory, "Output");
            }

            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs))
            {
                docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return Path.Combine(docs, "AllInOne PDF");
        }
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ThemesDirectory);
        Directory.CreateDirectory(PublicationsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(DraftsDirectory);
    }

    /// <summary>
    /// Resolves the data folder for an executable living in <paramref name="baseDirectory"/>.
    /// 1. <paramref name="environmentOverride"/> (the <c>ALLINONEPDF_DATA_DIR</c> variable) wins.
    /// 2. <c>portable.txt</c> beside the executable → portable; its first non-empty line is a relative data path (default <c>Data</c>).
    /// 3. PortableApps.com layout (<c>…/App/AllInOnePdf/</c> with a sibling <c>…/Data</c> folder or <c>…/App/AppInfo</c>) → portable, data in <c>…/Data</c>.
    /// 4. Otherwise the per-user application data folder.
    /// </summary>
    public static string ResolveDataRoot(string baseDirectory, string? environmentOverride, out bool isPortable)
    {
        isPortable = false;
        if (!string.IsNullOrWhiteSpace(environmentOverride))
        {
            return Path.GetFullPath(environmentOverride);
        }

        var baseDir = Path.GetFullPath(baseDirectory);
        var marker = Path.Combine(baseDir, PortableMarkerFileName);
        if (File.Exists(marker))
        {
            isPortable = true;
            string relative = "Data";
            try
            {
                var line = File.ReadLines(marker).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith(';'));
                if (!string.IsNullOrWhiteSpace(line))
                {
                    // The marker may have been written on the other OS ("..\Data" on Linux is otherwise one odd folder name).
                    relative = line.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                }
            }
            catch (IOException)
            {
            }

            return Path.GetFullPath(Path.Combine(baseDir, relative));
        }

        // PortableApps.com: <Root>/App/AllInOnePdf/<exe>, settings in <Root>/Data.
        var appDir = Directory.GetParent(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (appDir is { Name: var appName } && string.Equals(appName, "App", StringComparison.OrdinalIgnoreCase) && appDir.Parent is { } root)
        {
            var data = Path.Combine(root.FullName, "Data");
            if (Directory.Exists(data) || Directory.Exists(Path.Combine(appDir.FullName, "AppInfo")))
            {
                isPortable = true;
                return data;
            }
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(appData))
        {
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(appData, AppFolderName);
    }
}

/// <summary>Tiny atomic JSON file store used for preferences and history.</summary>
public sealed class JsonFileStore : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public void Dispose() => _gate.Dispose();

    public async Task<T?> ReadAsync<T>(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return default;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<T>(stream, CoreJson.Options, cancellationToken);
        }
        catch (JsonException)
        {
            return default;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync<T>(T value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(stream, value, CoreJson.Options, cancellationToken);
            }

            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
