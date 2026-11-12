using System.Diagnostics;
using AllInOnePdf.UI.Services;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.JSInterop;
using Photino.NET;

namespace AllInOnePdf.Desktop;

/// <summary>Desktop implementation of <see cref="IHostServices"/> backed by Photino's native dialogs and the OS shell.</summary>
public sealed class PhotinoHostServices : IHostServices
{
    private readonly PhotinoWindow _window;
    private readonly IJSRuntime _js;
    private readonly WindowFrame _frame;

    public PhotinoHostServices(PhotinoWindow window, IJSRuntime js, WindowFrame frame)
    {
        _window = window;
        _js = js;
        _frame = frame;
    }

    // ---- Window controls: the header's caption buttons and drag region (JS -> AppHeader -> here -> WindowFrame) ----

    public bool SupportsWindowControls => _frame.UseCustomTitleBar;

    public bool IsMaximized => _frame.IsMaximized;

    public event Action? WindowStateChanged
    {
        add => _frame.StateChanged += value;
        remove => _frame.StateChanged -= value;
    }

    public Task MinimizeWindowAsync() => OnWindowThread(_frame.Minimize);

    public Task ToggleMaximizeWindowAsync() => OnWindowThread(_frame.ToggleMaximize);

    public Task CloseWindowAsync() => OnWindowThread(_frame.Close);

    public Task MoveWindowByAsync(int dx, int dy) => OnWindowThread(() => _frame.MoveBy(dx, dy));

    public Task<bool> BeginWindowDragAsync(WindowDragOperation operation)
    {
        var started = false;
        _window.Invoke(() => started = _frame.BeginDrag(operation));
        return Task.FromResult(started);
    }

    private Task OnWindowThread(Action action)
    {
        _window.Invoke(action);
        return Task.CompletedTask;
    }

    public HostKind Kind => HostKind.Desktop;

    public bool SupportsNativeDialogs => true;

    public bool SupportsShellOpen => true;

    public Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<FileFilter>? filters = null, bool multiSelect = true, string? initialDirectory = null)
    {
        try
        {
            var result = _window.ShowOpenFile(SafeTitle(title), SafeDirectory(initialDirectory), multiSelect, ToPhotinoFilters(filters));
            return Task.FromResult<IReadOnlyList<string>>(result?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() ?? []);
        }
        catch (Exception)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        try
        {
            var result = _window.ShowOpenFolder(SafeTitle(title), SafeDirectory(initialDirectory), multiSelect: false);
            return Task.FromResult(result?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)));
        }
        catch (Exception)
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<string?> PickSaveFileAsync(string title, string suggestedFileName, IReadOnlyList<FileFilter>? filters = null, string? initialDirectory = null)
    {
        try
        {
            var name = Path.GetFileName(suggestedFileName);
            var dir = SafeDirectory(initialDirectory);
            var defaultPath = dir is null ? name : Path.Combine(dir, name);
            var result = _window.ShowSaveFile(SafeTitle(title), defaultPath, ToPhotinoFilters(filters));
            return Task.FromResult(string.IsNullOrWhiteSpace(result) ? null : result);
        }
        catch (Exception)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private static string SafeTitle(string title) =>
        string.IsNullOrWhiteSpace(title) ? "AllInOne PDF" : title.Length > 120 ? title[..120] : title;

    private static string? SafeDirectory(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) ? directory : null;

    public async Task SaveFileAsync(string suggestedFileName, byte[] content, string mediaType)
    {
        var ext = Path.GetExtension(suggestedFileName).TrimStart('.');
        var filters = string.IsNullOrEmpty(ext) ? null : new[] { new FileFilter(ext.ToUpperInvariant() + " file", [ext]) };
        var path = await PickSaveFileAsync("Save file", suggestedFileName, filters);
        if (path is null)
        {
            return;
        }

        await File.WriteAllBytesAsync(path, content);
    }

    public Task RevealInFolderAsync(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", ["-R", path]) { UseShellExecute = false });
            }
            else
            {
                var dir = File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;
                Process.Start(new ProcessStartInfo("xdg-open", [dir]) { UseShellExecute = false });
            }
        }
        catch (Exception)
        {
            // Shell integration is best-effort.
        }

        return Task.CompletedTask;
    }

    public Task OpenWithDefaultAppAsync(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return Task.CompletedTask;
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", [path]) { UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open", [path]) { UseShellExecute = false });
            }
        }
        catch (Exception)
        {
        }

        return Task.CompletedTask;
    }

    public Task OpenUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Task.CompletedTask;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", [uri.ToString()]) { UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open", [uri.ToString()]) { UseShellExecute = false });
            }
        }
        catch (Exception)
        {
        }

        return Task.CompletedTask;
    }

    public async Task CopyToClipboardAsync(string text)
    {
        // Photino has no clipboard API; the webview's clipboard works for the app's own origin.
        try
        {
            await _js.InvokeVoidAsync("aio.copyText", text);
        }
        catch (JSException)
        {
        }
    }

    /// <summary>
    /// Photino's native Windows dialog copies each filter into a fixed buffer; one filter with ~55 extensions
    /// crashes the process. <see cref="FileFilters.Normalize"/> splits long filters so every pattern stays short.
    /// </summary>
    private static (string Name, string[] Extensions)[]? ToPhotinoFilters(IReadOnlyList<FileFilter>? filters)
    {
        var safe = FileFilters.Normalize(filters);
        return safe.Count == 0 ? null : safe.Select(f => (f.Name, f.Extensions.ToArray())).ToArray();
    }
}
