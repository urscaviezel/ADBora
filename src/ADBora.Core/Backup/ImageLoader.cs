using SkiaSharp;

namespace AdbTool.Backup;

/// <summary>
/// Image helpers on top of SkiaSharp (PNG, JPEG, WebP, GIF, BMP – on Windows and Linux alike).
/// </summary>
internal static class ImageLoader
{
    private static readonly SKSamplingOptions HighQuality = new(SKCubicResampler.Mitchell);

    /// <summary>Decodes an image to a 32-bit premultiplied bitmap; null if the format is unknown.</summary>
    public static SKBitmap? Decode(byte[] data)
    {
        if (data.Length == 0)
            return null;
        try
        {
            using SKBitmap? decoded = SKBitmap.Decode(data);
            if (decoded is null) return null;
            // normalise the pixel format (palette / grey PNGs otherwise draw oddly)
            var bmp = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (!decoded.CopyTo(bmp, SKColorType.Bgra8888))
            {
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(decoded, 0, 0);
            }
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Empty, transparent 32-bit bitmap.</summary>
    public static SKBitmap NewBitmap(int width, int height)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        bmp.Erase(SKColors.Transparent);
        return bmp;
    }

    /// <summary>Draws <paramref name="source"/> (part <paramref name="src"/>, or all) into <paramref name="dest"/> with high quality.</summary>
    public static void Draw(SKCanvas canvas, SKBitmap source, SKRect dest, SKRect? src = null)
    {
        using SKImage image = SKImage.FromBitmap(source);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, src ?? new SKRect(0, 0, source.Width, source.Height), dest, HighQuality, paint);
    }

    /// <summary>Rounded-square clip as used for launcher icons (radius = size / 5).</summary>
    public static void ClipIcon(SKCanvas canvas, int size) =>
        canvas.ClipRoundRect(new SKRoundRect(new SKRect(0, 0, size, size), size / 5f), SKClipOperation.Intersect, antialias: true);

    public static byte[] EncodePng(SKBitmap bmp)
    {
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static void SavePng(SKBitmap bmp, string file)
    {
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream fs = File.Create(file);
        data.SaveTo(fs);
    }

    /// <summary>Scales an icon to a square thumbnail (high quality, centred, aspect kept).</summary>
    public static SKBitmap Thumbnail(SKBitmap source, int size)
    {
        SKBitmap bmp = NewBitmap(size, size);
        using var canvas = new SKCanvas(bmp);
        float scale = Math.Min((float)size / source.Width, (float)size / source.Height);
        float w = source.Width * scale, h = source.Height * scale;
        Draw(canvas, source, SKRect.Create((size - w) / 2, (size - h) / 2, w, h));
        return bmp;
    }
}
