using System.Runtime.InteropServices;
namespace CorePilot.Services;

// Only inspects the foreground window's bounds; no injected code or global hooks.
internal static class FullscreenWatcher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    public static bool IsFullscreenApplicationForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return false;
            var monitor = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
            return rect.Left <= monitor.Left + 1 && rect.Top <= monitor.Top + 1
                && rect.Right >= monitor.Right - 1 && rect.Bottom >= monitor.Bottom - 1;
        }
        catch { return false; }
    }
}
