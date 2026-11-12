using Microsoft.JSInterop;

namespace AllInOnePdf.UI.Services;

/// <summary>Host services for the Blazor Server web host: downloads via JS, no native dialogs.</summary>
public sealed class BrowserHostServices : IHostServices
{
    private readonly IJSRuntime _js;

    public BrowserHostServices(IJSRuntime js)
    {
        _js = js;
    }

    public HostKind Kind => HostKind.Browser;

    public bool SupportsNativeDialogs => false;

    public bool SupportsShellOpen => false;

    public Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<FileFilter>? filters = null, bool multiSelect = true, string? initialDirectory = null) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);

    public Task<string?> PickSaveFileAsync(string title, string suggestedFileName, IReadOnlyList<FileFilter>? filters = null, string? initialDirectory = null) =>
        Task.FromResult<string?>(null);

    /// <summary>Streams the bytes to the browser (<c>aio.downloadStream</c>) instead of building a base64 string.</summary>
    public async Task SaveFileAsync(string suggestedFileName, byte[] content, string mediaType)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var streamRef = new DotNetStreamReference(stream, leaveOpen: false);
        await _js.InvokeVoidAsync("aio.downloadStream", suggestedFileName, mediaType, streamRef);
    }

    public Task RevealInFolderAsync(string path) => Task.CompletedTask;

    public Task OpenWithDefaultAppAsync(string path) => Task.CompletedTask;

    public async Task OpenUrlAsync(string url)
    {
        await _js.InvokeVoidAsync("aio.openUrl", url);
    }

    public async Task CopyToClipboardAsync(string text)
    {
        await _js.InvokeVoidAsync("aio.copyText", text);
    }

    // The browser draws its own window: no caption buttons, the header does not move anything.
    public bool SupportsWindowControls => false;

    public bool IsMaximized => false;

    public event Action? WindowStateChanged
    {
        add { }
        remove { }
    }

    public Task MinimizeWindowAsync() => Task.CompletedTask;

    public Task ToggleMaximizeWindowAsync() => Task.CompletedTask;

    public Task CloseWindowAsync() => Task.CompletedTask;

    public Task MoveWindowByAsync(int dx, int dy) => Task.CompletedTask;

    public Task<bool> BeginWindowDragAsync(WindowDragOperation operation) => Task.FromResult(false);
}
