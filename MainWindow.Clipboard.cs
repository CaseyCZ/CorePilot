using System.Runtime.InteropServices;
using CorePilot.Services;

namespace CorePilot;

public partial class MainWindow
{
    private readonly ClipboardHistoryService _clipboardHistory = new();
    private bool _clipboardListenerRegistered;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private void StartClipboardListener()
    {
        if (_hwnd == IntPtr.Zero) return;
        _clipboardHistory.Enabled = _viewModel.Settings.Current.ClipboardEnabled;
        try { _clipboardListenerRegistered = AddClipboardFormatListener(_hwnd); }
        catch (Exception ex) { Infrastructure.Log.Write("Clipboard listener not available.", ex); }
    }

    private void StopClipboardListener()
    {
        if (!_clipboardListenerRegistered || _hwnd == IntPtr.Zero) return;
        try { RemoveClipboardFormatListener(_hwnd); }
        catch (Exception ex) { Infrastructure.Log.Write("Clipboard listener cleanup failed.", ex); }
        _clipboardListenerRegistered = false;
    }

    public void SetClipboardEnabled(bool enabled)
    {
        _viewModel.Settings.Current.ClipboardEnabled = enabled;
        _clipboardHistory.Enabled = enabled;
        _viewModel.Settings.Save();
    }
}
