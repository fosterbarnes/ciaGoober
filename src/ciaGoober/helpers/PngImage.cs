using System.IO;
using System.Windows.Media.Imaging;

namespace CiaGoober.Helpers;

public static class PngImage
{
    // Decodes now and freezes, so it can be built on any thread and shown on the UI thread.
    public static BitmapImage Load(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
