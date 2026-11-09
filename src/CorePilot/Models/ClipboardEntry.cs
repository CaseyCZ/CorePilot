using System.Windows.Media.Imaging;
namespace CorePilot.Models;
public sealed record ClipboardEntry(string Title, string? Text, BitmapSource? Picture, DateTime Timestamp)
{
    public bool IsImage => Picture is not null;
    public string Description => Timestamp.ToString("HH:mm:ss");
}
