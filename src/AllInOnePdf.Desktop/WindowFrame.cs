using System.Drawing;
using System.Runtime.InteropServices;
using AllInOnePdf.UI.Services;
using Photino.NET;

namespace AllInOnePdf.Desktop;

/// <summary>
/// The main window's frame: the app's own title bar (Windows) and the window operations behind its caption buttons.
/// </summary>
/// <remarks>
/// Photino's chromeless mode makes a bare <c>WS_POPUP</c> window on Windows: no resize edges, no snapping, no
/// Win+Arrow, and maximizing covers the taskbar. So the window keeps its normal frame and only the caption is removed:
/// the window procedure is subclassed and <c>WM_NCCALCSIZE</c> extends the client area over the title bar, keeping the
/// left, right and bottom resize borders (invisible on Windows 10/11). Moving and the top-edge resize are handed to the
/// OS (<c>WM_NCLBUTTONDOWN</c> with <c>HTCAPTION</c>/<c>HTTOP</c>), so dragging snaps, drags a maximized window out of
/// its maximized state and runs at the system's speed. Switching between the app's and the system title bar is a
/// flag plus <c>SWP_FRAMECHANGED</c>, so it works at runtime. Other platforms keep the system title bar.
/// </remarks>
public sealed class WindowFrame
{
    private readonly PhotinoWindow _window;
    private IntPtr _hwnd;
    private IntPtr _previousProc;
    private WndProc? _proc; // Rooted here so the GC never collects the delegate the window procedure points to.
    private bool _useCustomTitleBar;
    private bool _lastMaximized;

    public WindowFrame(PhotinoWindow window)
    {
        _window = window;
    }

    /// <summary>True on platforms where the app can draw the title bar (Windows).</summary>
    public static bool IsCustomTitleBarAvailable => OperatingSystem.IsWindows();

    /// <summary>Raised when <see cref="UseCustomTitleBar"/> or <see cref="IsMaximized"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>True while the app's own title bar replaces the system one.</summary>
    public bool UseCustomTitleBar => _useCustomTitleBar && _hwnd != IntPtr.Zero;

    public bool IsMaximized => _hwnd != IntPtr.Zero ? IsZoomed(_hwnd) : SafeGet(() => _window.Maximized);

    /// <summary>Installs the window procedure hook. Call once on the UI thread after the native window exists.</summary>
    public void Attach(bool useCustomTitleBar)
    {
        if (!IsCustomTitleBarAvailable || _hwnd != IntPtr.Zero)
        {
            return;
        }

        var hwnd = _window.WindowHandle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _proc = HookProc;
        var previous = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_proc));
        if (previous == IntPtr.Zero)
        {
            _proc = null;
            return;
        }

        _hwnd = hwnd;
        _previousProc = previous;
        _lastMaximized = IsZoomed(hwnd);
        _useCustomTitleBar = useCustomTitleBar;
        if (useCustomTitleBar)
        {
            RefreshFrame();
        }
    }

    /// <summary>Switches between the app's title bar and the system one. Call on the UI thread.</summary>
    public void SetUseCustomTitleBar(bool value)
    {
        if (_hwnd == IntPtr.Zero || _useCustomTitleBar == value)
        {
            return;
        }

        _useCustomTitleBar = value;
        RefreshFrame();
        StateChanged?.Invoke();
    }

    public void Minimize()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WmSysCommand, ScMinimize, IntPtr.Zero);
            return;
        }

        _window.SetMinimized(true);
    }

    public void ToggleMaximize()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WmSysCommand, IsZoomed(_hwnd) ? ScRestore : ScMaximize, IntPtr.Zero);
            return;
        }

        _window.SetMaximized(!_window.Maximized);
    }

    /// <summary>Closes the window the way Alt+F4 does (posted, so the calling Blazor event finishes first).</summary>
    public void Close()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WmSysCommand, ScClose, IntPtr.Zero);
            return;
        }

        _window.Close();
    }

    public void MoveBy(int dx, int dy)
    {
        var location = _window.Location;
        _window.SetLocation(new Point(location.X + dx, location.Y + dy));
    }

    /// <summary>Hands the drag under way to the OS move/size loop. False when not available.</summary>
    public bool BeginDrag(WindowDragOperation operation)
    {
        if (_hwnd == IntPtr.Zero || !GetCursorPos(out var cursor))
        {
            return false;
        }

        var hit = operation switch
        {
            WindowDragOperation.ResizeTop => HtTop,
            WindowDragOperation.ResizeTopLeft => HtTopLeft,
            WindowDragOperation.ResizeTopRight => HtTopRight,
            _ => HtCaption,
        };

        // The webview holds the mouse capture while the button is down; release it so the OS loop takes over.
        ReleaseCapture();
        var lParam = (IntPtr)((cursor.Y << 16) | (cursor.X & 0xFFFF));
        return PostMessage(_hwnd, WmNcLButtonDown, hit, lParam);
    }

    private void RefreshFrame() =>
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpFrameChanged | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);

    private IntPtr HookProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WmNcCalcSize && wParam != IntPtr.Zero && _useCustomTitleBar)
            {
                // Let Windows work out the standard frame, then give the caption back to the client area.
                var original = Marshal.PtrToStructure<Rect>(lParam);
                var result = CallWindowProc(_previousProc, hwnd, msg, wParam, lParam);
                var client = Marshal.PtrToStructure<Rect>(lParam);
                client.Top = original.Top;

                // A maximized window hangs over the monitor by its frame thickness on every side; the side frame
                // that Windows just removed tells how much, so the top edge stays on screen.
                if (IsZoomed(hwnd))
                {
                    client.Top += client.Left - original.Left;
                }

                Marshal.StructureToPtr(client, lParam, fDeleteOld: false);
                return result;
            }

            if (msg == WmSize)
            {
                var maximized = IsZoomed(hwnd);
                if (maximized != _lastMaximized)
                {
                    _lastMaximized = maximized;
                    var result = CallWindowProc(_previousProc, hwnd, msg, wParam, lParam);
                    StateChanged?.Invoke();
                    return result;
                }
            }
        }
        catch (Exception)
        {
            // Never let an exception unwind through the native window procedure.
        }

        return CallWindowProc(_previousProc, hwnd, msg, wParam, lParam);
    }

    private static bool SafeGet(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- Win32 ------------------------------------------------------------------------------------------------------

    private const int GwlpWndProc = -4;
    private const uint WmSize = 0x0005;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcLButtonDown = 0x00A1;
    private const uint WmSysCommand = 0x0112;
    private static readonly IntPtr ScMinimize = 0xF020;
    private static readonly IntPtr ScMaximize = 0xF030;
    private static readonly IntPtr ScClose = 0xF060;
    private static readonly IntPtr ScRestore = 0xF120;
    private static readonly IntPtr HtCaption = 2;
    private static readonly IntPtr HtTop = 12;
    private static readonly IntPtr HtTopLeft = 13;
    private static readonly IntPtr HtTopRight = 14;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : (IntPtr)SetWindowLong32(hwnd, index, value.ToInt32());

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
