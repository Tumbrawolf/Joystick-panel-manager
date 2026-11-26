using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudFlight.App;

/// <summary>Turns any picture file into the small JPEG a key screen takes, and back into a
/// thumbnail for the UI.</summary>
internal static class KeyImages
{
    /// <summary>Loads an image file (PNG, JPEG, BMP, GIF, TIFF, ICO...) and fits it, keeping its
    /// shape, onto a size x size black square, encoded as a baseline JPEG. Throws if the file can't
    /// be read as an image.</summary>
    public static byte[] FromFile(string path, int size)
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad; // read it all now so the file isn't kept open
        source.UriSource = new Uri(path, UriKind.Absolute);
        source.EndInit();

        var scale = Math.Min((double)size / source.PixelWidth, (double)size / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen())
        {
            // Transparent areas come out black, which is what an unlit key screen looks like.
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, size, size));
            context.DrawImage(source, new Rect((size - width) / 2, (size - height) / 2, width, height));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Decodes a stored key image for display, or null if it can't be decoded.</summary>
    public static ImageSource? ToThumbnail(byte[] jpeg)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(jpeg);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException)
        {
            return null;
        }
    }
}
