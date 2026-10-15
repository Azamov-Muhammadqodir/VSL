using System.Windows.Media.Imaging;

// Attachment thumbnails: the shared item model decodes them with Avalonia's Bitmap; here WPF decodes them.
namespace Avalonia.Media.Imaging;

public sealed class Bitmap
{
    Bitmap(BitmapSource source) => Source = source;

    /// <summary>The decoded image, for an Image in the WPF view.</summary>
    public BitmapSource Source { get; }

    public static Bitmap DecodeToWidth(Stream stream, int width)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return new Bitmap(image);
    }
}
