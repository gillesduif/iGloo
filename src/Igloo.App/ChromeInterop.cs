using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

// The P/Invokes below target dwmapi/user32 (system DLLs resolved from System32).
// Pinning the search path defeats DLL-preloading (hijack) attacks.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Igloo.App;

/// <summary>
/// Win32 interop backing the custom window chrome on <see cref="MainWindow"/>.
///
/// Three jobs, none of which WPF + <c>WindowChrome</c> give us for free:
///   1. Paint any momentary native chrome (startup, before the WPF template applies)
///      dark instead of white — <see cref="EnableDarkTitleBar"/>.
///   2. Constrain a <c>WindowStyle=None</c> maximized window to the monitor work area
///      so it never covers the taskbar or clips content — <see cref="ConstrainMaximizedBounds"/>.
///   3. Restore the native system menu (Alt+Space, caption right-click) that
///      <c>WindowStyle=None</c> otherwise removes — <see cref="ShowSystemMenu"/>.
/// </summary>
internal static partial class ChromeInterop
{
    //   Window messages                            ─
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCMOUSEMOVE = 0x00A0;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_NCLBUTTONUP = 0x00A2;
    public const int WM_NCRBUTTONUP = 0x00A5;
    public const int WM_NCMOUSELEAVE = 0x02A2;
    public const int WM_SYSKEYDOWN = 0x0104;

    public const int HTCAPTION = 2;
    public const int HTMAXBUTTON = 9;
    public const int VK_SPACE = 0x20;

    //   Dark title bar                             

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // Win10 2004+ / Win11
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1 = 19; // Win10 1809–1909

    /// <summary>
    /// Asks DWM to render this window's native non-client area in dark mode. Only matters
    /// for the brief moments WPF's custom chrome is not yet painting (startup, restore
    /// transitions); the rest of the time our own title bar covers it. Safe no-op on
    /// builds that don't recognise the attribute.
    /// </summary>
    public static void EnableDarkTitleBar(IntPtr hwnd)
    {
        int enabled = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, ref enabled, sizeof(int));
    }

    //   Mica backdrop (the real thing, not a painted imitation)        

    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;    // Win11 22H2+
    private const int DWMWA_MICA_EFFECT = 1029;  // Win11 21H2 (undocumented)
    private const int DWMSBT_MAINWINDOW = 2;     // Mica

    /// <summary>
    /// Asks DWM to render the system Mica backdrop behind this window — the same
    /// desktop-tinted material Settings and other first-party Win11 apps use.
    /// The caller must make the window background transparent so it shows
    /// through. Returns false on Windows 10 (caller keeps a solid fallback).
    /// </summary>
    public static bool TryEnableMicaBackdrop(IntPtr hwnd)
    {
        int type = DWMSBT_MAINWINDOW;
        if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0)
            return true;

        int on = 1;   // 21H2 fallback flag
        return DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref on, sizeof(int)) == 0;
    }

    //   Maximize bounds (the classic WindowStyle=None bug)           

    /// <summary>
    /// Handles <c>WM_GETMINMAXINFO</c>: pins a maximized window to the current monitor's
    /// work area (excluding the taskbar) and re-applies the window's minimum size, so a
    /// borderless window neither overlaps the taskbar nor clips its own content.
    /// Multi-monitor aware via <c>MonitorFromWindow</c>.
    /// </summary>
    public static void ConstrainMaximizedBounds(IntPtr hwnd, IntPtr lParam, double minWidthDip, double minHeightDip)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi))
            return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        // Maximized position/size in coordinates relative to the monitor.
        mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
        mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
        mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
        mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;

        // We answer WM_GETMINMAXINFO fully (handled=true), so re-assert the min size
        // ourselves — otherwise the user could shrink the window past MinWidth/MinHeight.
        var scale = DpiScale(hwnd);
        mmi.ptMinTrackSize.X = (int)(minWidthDip * scale);
        mmi.ptMinTrackSize.Y = (int)(minHeightDip * scale);

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    //   System menu                              ─

    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint MF_ENABLED = 0x0000, MF_GRAYED = 0x0001;
    private const uint SC_SIZE = 0xF000, SC_MOVE = 0xF010, SC_MINIMIZE = 0xF020,
                       SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120, SC_CLOSE = 0xF060;
    private const uint TPM_RETURNCMD = 0x0100, TPM_LEFTBUTTON = 0x0000;

    /// <summary>
    /// Pops the native system menu (Restore / Move / Size / Minimize / Maximize / Close)
    /// at a screen location given in physical pixels, with items enabled/greyed to match
    /// the current window state. Used for Alt+Space and caption right-click, both of which
    /// <c>WindowStyle=None</c> would otherwise swallow.
    /// </summary>
    public static void ShowSystemMenu(Window window, int screenX, int screenY)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var menu = GetSystemMenu(hwnd, false);
        if (menu == IntPtr.Zero)
            return;

        var maximized = window.WindowState == WindowState.Maximized;
        // EnableMenuItem returns the item's PREVIOUS enabled/greyed state, not an error
        // code; the standard system-menu items always exist, so the prior state is of no
        // use here. Discard explicitly rather than silently dropping the result.
        _ = EnableMenuItem(menu, SC_RESTORE, maximized ? MF_ENABLED : MF_GRAYED);
        _ = EnableMenuItem(menu, SC_MOVE, maximized ? MF_GRAYED : MF_ENABLED);
        _ = EnableMenuItem(menu, SC_SIZE, maximized ? MF_GRAYED : MF_ENABLED);
        _ = EnableMenuItem(menu, SC_MINIMIZE, MF_ENABLED);
        _ = EnableMenuItem(menu, SC_MAXIMIZE, maximized ? MF_GRAYED : MF_ENABLED);
        _ = EnableMenuItem(menu, SC_CLOSE, MF_ENABLED);

        var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_LEFTBUTTON, screenX, screenY, hwnd, IntPtr.Zero);
        if (cmd != 0 && !PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero))
            System.Diagnostics.Debug.WriteLine($"PostMessage(WM_SYSCOMMAND {cmd:X}) failed: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>Splits the screen coordinates packed into an NC mouse-message lParam.</summary>
    public static (int X, int Y) GetScreenPoint(IntPtr lParam)
    {
        var v = lParam.ToInt32();
        return ((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }

    private static double DpiScale(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    //   P/Invoke                                

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    // user32 exports GetMonitorInfoA/W, not "GetMonitorInfo". [DllImport] auto-probed the
    // suffix (ExactSpelling=false); [LibraryImport] is always ExactSpelling=true, so the
    // W entry point must be named explicitly or the call throws EntryPointNotFoundException.
    // Plain MONITORINFO has no string fields, so the W variant needs no extra marshalling.
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetSystemMenu(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool revert);

    [LibraryImport("user32.dll")]
    private static partial int EnableMenuItem(IntPtr hMenu, uint item, uint enable);

    [LibraryImport("user32.dll")]
    private static partial uint TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    // Same A/W entry-point rule as GetMonitorInfo: user32 exports PostMessageA/W only.
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
