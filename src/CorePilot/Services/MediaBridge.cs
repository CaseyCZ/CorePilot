using System.IO;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace CorePilot.Services;
public sealed record MediaSnapshot(string Title, string Artist, BitmapSource? Artwork,
    bool Playing, TimeSpan Position, TimeSpan Duration, bool Available)
{
    public static MediaSnapshot Empty { get; } = new("Nic se nepřehrává", "Spusť hudbu nebo video",
        null, false, TimeSpan.Zero, TimeSpan.Zero, false);
}
public sealed class MediaBridge
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _artworkKey;
    private BitmapSource? _artwork;

    public async Task<MediaSnapshot> RefreshAsync()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _manager.GetCurrentSession();
            if (session is null) { _session = null; _artwork = null; _artworkKey = null; return MediaSnapshot.Empty; }
            _session = session;
            var details = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var duration = timeline.EndTime - timeline.StartTime;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            var position = timeline.Position - timeline.StartTime;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            var key = $"{session.SourceAppUserModelId}|{details.Title}|{details.Artist}";
            if (_artworkKey != key) { _artworkKey = key; _artwork = await DecodeArtworkAsync(details.Thumbnail); }
            return new MediaSnapshot(
                CleanMediaText(details.Title, "Neznámá skladba"),
                CleanMediaText(details.Artist, "Interpret není uveden"),
                _artwork, playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                position, duration, true);
        }
        catch { _session = null; _artworkKey = null; _artwork = null; return MediaSnapshot.Empty; }
    }
    private static string CleanMediaText(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Contains('\uFFFD') || string.IsNullOrWhiteSpace(text)) return fallback;
        return text.Length > 160 ? text[..160] : text;
    }
    private static async Task<BitmapSource?> DecodeArtworkAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024) return null;
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = new MemoryStream(bytes, writable: false);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }
    public async Task ToggleAsync() { if (_session is not null) await _session.TryTogglePlayPauseAsync(); }
    public async Task NextAsync() { if (_session is not null) await _session.TrySkipNextAsync(); }
    public async Task PreviousAsync() { if (_session is not null) await _session.TrySkipPreviousAsync(); }
    public async Task SeekAsync(TimeSpan position) { if (_session is not null) await _session.TryChangePlaybackPositionAsync(position.Ticks); }
}
