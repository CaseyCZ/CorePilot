using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CorePilot.Infrastructure;

public static class ImageDecoder
{
    private const int MaxDecodeWidth = 512;

    public static ImageSource? FromBytes(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 24 * 1024 * 1024)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = MaxDecodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Write("Image could not be decoded.", ex);
            return null;
        }
    }
}
