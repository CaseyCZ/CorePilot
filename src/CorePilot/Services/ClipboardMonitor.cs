using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CorePilot.Models;
namespace CorePilot.Services;

public sealed class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private bool _listening, _disposed;
    public ObservableCollection<ClipboardEntry> Entries { get; } = [];
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    public ClipboardMonitor(Window owner)
    {
        _handle = new WindowInteropHelper(owner).Handle;
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("Window handle is not ready.");
        _source.AddHook(WndProc);
    }
    public void SetEnabled(bool enabled)
    {
        if (_disposed || _listening == enabled) return;
        if (enabled) _listening = AddClipboardFormatListener(_handle);
        else if (RemoveClipboardFormatListener(_handle)) _listening = false;
    }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _listening) Capture();
        return IntPtr.Zero;
    }
    private void Capture()
    {
        try {
            ClipboardEntry? entry = null;
            if (System.Windows.Clipboard.ContainsImage()) {
                BitmapSource? image = System.Windows.Clipboard.GetImage();
                if (image is not null && image.PixelWidth > 0 && image.PixelHeight > 0
                    && (long)image.PixelWidth * image.PixelHeight <= 4_000_000) {
                    image.Freeze();
                    entry = new ClipboardEntry($"Obrázek {image.PixelWidth} × {image.PixelHeight}", null, image, DateTime.Now);
                }
            } else if (System.Windows.Clipboard.ContainsText()) {
                string value = System.Windows.Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(value) && value.Length <= 100_000) {
                    string title = value.Replace('\r', ' ').Replace('\n', ' ');
                    if (title.Length > 78) title = title[..78] + "…";
                    entry = new ClipboardEntry(title, value, null, DateTime.Now);
                }
            }
            if (entry is null) return;
            if (Entries.Count > 0 && entry.Text is not null && Entries[0].Text == entry.Text) return;
            Entries.Insert(0, entry);
            while (Entries.Count > 20) Entries.RemoveAt(Entries.Count - 1);
            while (Entries.Count(e => e.IsImage) > 5) {
                var old = Entries.LastOrDefault(e => e.IsImage);
                if (old is null) break;
                Entries.Remove(old);
            }
        } catch (ExternalException) { } catch (OutOfMemoryException) { }
    }
    public void Restore(ClipboardEntry entry)
    {
        try { if (entry.IsImage) System.Windows.Clipboard.SetImage(entry.Picture!); else if (entry.Text is not null) System.Windows.Clipboard.SetText(entry.Text); }
        catch (ExternalException) { }
    }
    public void Clear() => Entries.Clear();
    public void Dispose()
    {
        if (_disposed) return;
        if (_listening) RemoveClipboardFormatListener(_handle);
        try { _source.RemoveHook(WndProc); } catch (ObjectDisposedException) { }
        _disposed = true;
    }
}
