namespace AllInOnePdf.UI.Services;

public enum HostKind
{
    /// <summary>Photino desktop window: native file dialogs, direct disk access.</summary>
    Desktop,
    /// <summary>Blazor Server in a browser: uploads and downloads instead of paths.</summary>
    Browser,
}

public sealed record FileFilter(string Name, IReadOnlyList<string> Extensions);

/// <summary>What a press-and-drag on the app's own title bar does (see <see cref="IHostServices.BeginWindowDragAsync"/>).</summary>
public enum WindowDragOperation
{
    /// <summary>Moves the window (the empty parts of the title bar).</summary>
    Move,
    /// <summary>Resizes from the top edge.</summary>
    ResizeTop,
    /// <summary>Resizes from the top-left corner.</summary>
    ResizeTopLeft,
    /// <summary>Resizes from the top-right corner.</summary>
    ResizeTopRight,
}

/// <summary>
/// Everything the UI needs from the host that differs between the desktop window and the browser.
/// Components talk to this; hosts provide the implementation.
/// </summary>
public interface IHostServices
{
    HostKind Kind { get; }

    /// <summary>True when the host can open native pick/save dialogs that return file system paths.</summary>
    bool SupportsNativeDialogs { get; }

    /// <summary>True when the host can open files and folders in the OS (reveal, open with default app).</summary>
    bool SupportsShellOpen { get; }

    /// <summary>Native open-file dialog. Returns absolute paths, or empty when cancelled/unsupported.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<FileFilter>? filters = null, bool multiSelect = true, string? initialDirectory = null);

    /// <summary>Native pick-folder dialog. Returns null when cancelled/unsupported.</summary>
    Task<string?> PickFolderAsync(string title, string? initialDirectory = null);

    /// <summary>Native save-as dialog. Returns the chosen path or null when cancelled/unsupported.</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedFileName, IReadOnlyList<FileFilter>? filters = null, string? initialDirectory = null);

    /// <summary>Hands a file to the user: save dialog + write on desktop, browser download on the web.</summary>
    Task SaveFileAsync(string suggestedFileName, byte[] content, string mediaType);

    /// <summary>Shows the file in the OS file manager (Explorer / Files). No-op in the browser.</summary>
    Task RevealInFolderAsync(string path);

    /// <summary>Opens a file with its default application. No-op in the browser.</summary>
    Task OpenWithDefaultAppAsync(string path);

    /// <summary>Opens a URL in the system browser (desktop) or a new tab (web).</summary>
    Task OpenUrlAsync(string url);

    /// <summary>Copies text to the clipboard.</summary>
    Task CopyToClipboardAsync(string text);

    // ---- Window controls (the app's own title bar) --------------------------------------------------------------

    /// <summary>
    /// True when the app draws the window's title bar itself: the header shows Minimize, Maximize/Restore and Close
    /// and its empty parts move the window. False in the browser and when the system title bar is in use.
    /// </summary>
    bool SupportsWindowControls { get; }

    /// <summary>True while the window is maximized. Always false in the browser.</summary>
    bool IsMaximized { get; }

    /// <summary>
    /// Raised when <see cref="SupportsWindowControls"/> or <see cref="IsMaximized"/> may have changed. May fire off the
    /// renderer's thread; handlers use <c>InvokeAsync</c>.
    /// </summary>
    event Action? WindowStateChanged;

    Task MinimizeWindowAsync();

    /// <summary>Maximizes the window, or restores it when it is maximized.</summary>
    Task ToggleMaximizeWindowAsync();

    Task CloseWindowAsync();

    /// <summary>Moves the window by a delta in device pixels (the fallback when <see cref="BeginWindowDragAsync"/> returns false).</summary>
    Task MoveWindowByAsync(int dx, int dy);

    /// <summary>
    /// Hands a drag that is under way (primary button held) to the operating system, which then moves or resizes the
    /// window until the button is released, with snapping. Returns false when the host cannot; the caller then moves
    /// the window itself with <see cref="MoveWindowByAsync"/>.
    /// </summary>
    Task<bool> BeginWindowDragAsync(WindowDragOperation operation);
}
