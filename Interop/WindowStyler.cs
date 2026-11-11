using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CorePilot.Interop;

internal static class WindowStyler
{
    public static void MakeOverlay(IntPtr hwnd)
    {
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        ex |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate | NativeMethods.WsExLayered;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(ex));
    }

    public static void SetClickThrough(IntPtr hwnd, bool enabled)
    {
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        var updated = enabled
            ? ex | NativeMethods.WsExTransparent
            : ex & ~NativeMethods.WsExTransparent;

        if (updated != ex)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(updated));
        }
    }

    /// <summary>
    /// Compact overview lives INSIDE the top/bottom Windows taskbar bounds.
    /// Expanded controls live in the adjacent desktop working area.
    /// This is a taskbar overlay, not injection into Explorer.
    /// </summary>
    public static void PositionByTaskbar(IntPtr hwnd, double windowWidthDips,
                                         double windowHeightDips, double islandHeightDips,
                                         bool compact)
    {
        var screen = Screen.PrimaryScreen;
        if (screen is null) return;
        var bounds = screen.Bounds;
        var work = screen.WorkingArea;
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var width = Math.Max(1, (int)Math.Round(windowWidthDips * scale));
        var height = Math.Max(1, (int)Math.Round(windowHeightDips * scale));
        var islandHeight = Math.Max(1, (int)Math.Round(islandHeightDips * scale));
        var topBand = work.Top - bounds.Top;
        var bottomBand = bounds.Bottom - work.Bottom;
        var x = work.Left + (work.Width - width) / 2;
        int y;
        if (topBand >= 24 && topBand >= bottomBand)
            y = compact ? bounds.Top + Math.Max(0, (topBand - islandHeight) / 2) : work.Top;
        else if (bottomBand >= 24 && bottomBand > topBand)
            y = compact ? work.Bottom + Math.Max(0, (bottomBand - islandHeight) / 2)
                        : Math.Max(work.Top, work.Bottom - islandHeight - 26);
        else
            y = work.Top;

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, x, y, width, height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow | NativeMethods.SwpNoOwnerZOrder);
    }

    public static bool IsCursorOverTaskbarHotspot(int x, int y)
    {
        var screen = Screen.PrimaryScreen;
        if (screen is null) return false;
        var bounds = screen.Bounds;
        var work = screen.WorkingArea;
        var topBand = work.Top - bounds.Top;
        var bottomBand = bounds.Bottom - work.Bottom;
        var cx = work.Left + work.Width / 2;
        if (Math.Abs(x - cx) > 190) return false;
        if (topBand >= 24 && topBand >= bottomBand)
            return y >= bounds.Top && y < work.Top;
        if (bottomBand >= 24 && bottomBand > topBand)
            return y >= work.Bottom && y < bounds.Bottom;
        return false;
    }

    public static void SetKeyboardNavigation(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero) return;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        var next = enabled ? style & ~NativeMethods.WsExNoActivate
                           : style | NativeMethods.WsExNoActivate;
        if (next != style)
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(next));
    }

    public static System.Windows.Point GetCursorPositionDips()
    {
        if (!NativeMethods.GetCursorPos(out var p))
        {
            return new System.Windows.Point(-10000, -10000);
        }

        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        var scale = g.DpiX / 96.0;
        return new System.Windows.Point(p.X / scale, p.Y / scale);
    }

    public static bool IsForegroundFullscreen(IntPtr currentHwnd)
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == currentHwnd)
        {
            return false;
        }

        if (!NativeMethods.IsWindowVisible(fg))
        {
            return false;
        }

        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(fg, sb, sb.Capacity);
        var cls = sb.ToString();

        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "MultitaskingViewFrame")
        {
            return false;
        }

        var currentMonitor = NativeMethods.MonitorFromWindow(currentHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var fgMonitor = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);

        if (currentMonitor == IntPtr.Zero || fgMonitor == IntPtr.Zero || currentMonitor != fgMonitor)
        {
            return false;
        }

        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(currentMonitor, ref mi))
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(fg, out var fgRect))
        {
            return false;
        }

        return fgRect.Left <= mi.rcMonitor.Left &&
               fgRect.Top <= mi.rcMonitor.Top &&
               fgRect.Right >= mi.rcMonitor.Right &&
               fgRect.Bottom >= mi.rcMonitor.Bottom;
    }

    public static bool? IsForegroundBrowser(IntPtr currentHwnd)
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == currentHwnd)
        {
            return null;
        }

        try
        {
            NativeMethods.GetWindowThreadProcessId(fg, out var pid);
            if (pid == 0 || pid == Environment.ProcessId)
            {
                return null;
            }

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            return name.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("brave", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("opera", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("vivaldi", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }
}
