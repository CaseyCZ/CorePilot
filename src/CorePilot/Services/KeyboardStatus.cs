using System.Runtime.InteropServices;
namespace CorePilot.Services;
internal static class KeyboardStatus
{
    [DllImport("user32.dll")] private static extern short GetKeyState(int virtualKey);
    public static bool CapsLock => (GetKeyState(0x14) & 1) != 0;
    public static bool NumLock => (GetKeyState(0x90) & 1) != 0;
}
