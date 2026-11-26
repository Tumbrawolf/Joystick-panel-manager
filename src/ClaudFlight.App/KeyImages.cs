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

        return Encode(visual, size);
    }

    /// <summary>A name picture for a key with no picture of its own: its name in large type with
    /// what it's mapped to underneath, white on black, shrunk to fit.</summary>
    public static byte[] FromText(string title, string detail, int size)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var detailTypeface = new Typeface("Segoe UI");
        const double margin = 3;
        var width = size - 2 * margin;

        // Shrink the name until it fits in at most two lines over the top part of the key.
        FormattedText? titleText = null;
        if (title.Length > 0)
        {
            for (var fontSize = 16.0; fontSize >= 8; fontSize -= 1)
            {
                titleText = Text(title, typeface, fontSize, Brushes.White, width);
                titleText.MaxLineCount = 2;
                titleText.Trimming = TextTrimming.CharacterEllipsis;
                if (titleText.Height <= size * 0.55 && !Overflows(title, typeface, fontSize, width)) break;
            }
        }

        FormattedText? detailText = null;
        if (detail.Length > 0)
        {
            detailText = Text(detail, detailTypeface, 9, new SolidColorBrush(Color.FromRgb(0x9C, 0xD3, 0xFF)), width);
            detailText.MaxTextHeight = Math.Max(11, size - 2 * margin - (titleText?.Height ?? 0) - 2);
            detailText.Trimming = TextTrimming.CharacterEllipsis;
        }

        var total = (titleText?.Height ?? 0) + (detailText is null ? 0 : detailText.Height + 2);
        var y = Math.Max(margin, (size - total) / 2);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, size, size));
            if (titleText is not null)
            {
                context.DrawText(titleText, new Point(margin, y));
                y += titleText.Height + 2;
            }
            if (detailText is not null) context.DrawText(detailText, new Point(margin, y));
        }
        return Encode(visual, size);
    }

    /// <summary>The "lit up while pressed" version of a key's picture: the picture (or a blank key)
    /// brightened, with a bright frame around it.</summary>
    public static byte[] Highlight(byte[]? normal, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var area = new Rect(0, 0, size, size);
            context.DrawRectangle(Brushes.Black, null, area);
            if (normal is not null && Decode(normal) is { } picture) context.DrawImage(picture, area);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), null, area);
            const double frame = 5;
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x00)), frame),
                new Rect(frame / 2, frame / 2, size - frame, size - frame));
        }
        return Encode(visual, size);
    }

    private static FormattedText Text(string text, Typeface typeface, double fontSize, Brush brush, double width) =>
        new(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, brush, 1.0)
        {
            MaxTextWidth = width,
            TextAlignment = TextAlignment.Center,
        };

    /// <summary>True if a single word of the text is wider than the key at this size (it would be
    /// broken mid-word).</summary>
    private static bool Overflows(string text, Typeface typeface, double fontSize, double width) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word =>
            new FormattedText(word, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.White, 1.0).Width > width);

    private static byte[] Encode(DrawingVisual visual, int size)
    {
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Decodes stored picture bytes (a key image, a layout background) for display, or null
    /// if they can't be decoded.</summary>
    public static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
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
