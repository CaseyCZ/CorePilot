using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using CorePilot.Infrastructure;

namespace CorePilot.Services;

/// <summary>Only keeps a short, optional clipboard history in memory. Never writes content to disk.</summary>
public sealed class ClipboardHistoryService : IDisposable
{
    private const int MaxItems = 12;
    private const int MaxTextLength = 65536;
    private const long MaxImagePixels = 2_000_000;
    private bool _enabled;
    private bool _skipNextUpdate;

    public ObservableCollection<ClipboardHistoryItem> Items { get; } = new();
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) Clear();
        }
    }

    public void Capture()
    {
        if (!Enabled) return;
        if (_skipNextUpdate) { _skipNextUpdate = false; return; }

        try
        {
            ClipboardHistoryItem? entry = null;
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is not null && (long)image.PixelWidth * image.PixelHeight <= MaxImagePixels)
                {
                    if (image.CanFreeze) image.Freeze();
                    entry = new ClipboardHistoryItem(
                        LanguageManager.T("Image"), $"{image.PixelWidth} × {image.PixelHeight} px", null, image);
                }
            }
            else if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (!string.IsNullOrWhiteSpace(text) && text.Length <= MaxTextLength)
                {
                    var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    var preview = line.Length > 75 ? line[..75] + "…" : line;
                    entry = new ClipboardHistoryItem(preview,
                        $"{text.Length} {LanguageManager.T("Characters")}", text, null);
                }
            }
            if (entry is null) return;
            if (Items.Count > 0 && Items[0].Text == entry.Text && entry.Text is not null) return;
            Items.Insert(0, entry);
            while (Items.Count > MaxItems) Items.RemoveAt(Items.Count - 1);
        }
        catch (ExternalException) { } // Other process currently owns the clipboard.
        catch (Exception ex) { Log.Write("Clipboard event could not be read.", ex); }
    }

    public void Restore(ClipboardHistoryItem item)
    {
        if (!Enabled) return;
        try
        {
            if (item.Image is not null)
            {
                Clipboard.SetImage(item.Image);
            }
            else if (item.Text is not null)
            {
                Clipboard.SetText(item.Text, TextDataFormat.UnicodeText);
            }
            else return;
            _skipNextUpdate = true;
        }
        catch (Exception ex) { _skipNextUpdate = false; Log.Write("Clipboard item could not be restored.", ex); }
    }

    public void Clear() { _skipNextUpdate = false; Items.Clear(); }
    public void Dispose() => Clear();
}

public sealed record ClipboardHistoryItem(string Title, string Description, string? Text, BitmapSource? Image)
{
    public bool HasImage => Image is not null;
}
