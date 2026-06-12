using System.Runtime.InteropServices;

namespace AmbientFx.Hosting;

/// <summary>
/// Win32 interop for the hosting layer: extended window styles for effect windows
/// (no Alt-Tab, no activation), device-pixel window placement on a specific monitor,
/// and work-area-aware maximize clamping for the borderless control window.
/// x64-only process (PlatformTarget x64), so the *Ptr entry points always exist.
/// </summary>
internal static class NativeMethods
{
    internal const int GWL_EXSTYLE = -20;
    internal const long WS_EX_TOOLWINDOW = 0x00000080;
    internal const long WS_EX_NOACTIVATE = 0x08000000;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    internal const int WM_GETMINMAXINFO = 0x0024;
    internal const uint MONITOR_DEFAULTTONEAREST = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    /// <summary>Adds extended window styles (WS_EX_*) to a window. Call after the HWND exists (SourceInitialized).</summary>
    internal static void AddExtendedStyle(IntPtr hwnd, long exStyle)
    {
        long current = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(current | exStyle));
    }

    /// <summary>
    /// WM_GETMINMAXINFO handler body for a WindowStyle=None window: clamps the maximized
    /// rect to the current monitor's work area (a borderless WindowChrome window otherwise
    /// over-expands past the work area and covers the taskbar) and re-applies the minimum
    /// tracking size in device pixels (handling this message bypasses WPF's own
    /// MinWidth/MinHeight translation). Call from an HwndSource hook, then set handled=true.
    /// </summary>
    internal static void ClampMaximizedToWorkArea(IntPtr hwnd, IntPtr lParam, double minTrackWidthPx, double minTrackHeightPx)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                // ptMaxPosition is relative to the monitor origin, not the virtual desktop.
                mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
                mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
                mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
                mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
            }
        }

        mmi.ptMinTrackSize.X = (int)Math.Ceiling(minTrackWidthPx);
        mmi.ptMinTrackSize.Y = (int)Math.Ceiling(minTrackHeightPx);

        Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
    }
}
