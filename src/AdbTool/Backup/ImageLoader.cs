using System.Drawing.Imaging;

namespace AdbTool.Backup;

/// <summary>
/// Decodes PNG/JPEG with GDI+ and everything else (e.g. WebP) with the
/// Windows Imaging Component via WPF.
/// </summary>
internal static class ImageLoader
{
    public static Bitmap? Decode(byte[] data)
    {
        if (data.Length == 0)
            return null;

        try
        {
            using var ms = new MemoryStream(data);
            using Image image = Image.FromStream(ms);
            return new Bitmap(image);
        }
        catch
        {
            // not a GDI+ format
        }

        // Other formats (e.g. WebP) via the Windows Imaging Component (WPF).
        // This runs on a separate, short-lived STA thread: creating WPF objects
        // on the UI thread would attach a WPF dispatcher to it, which disturbs
        // the painting of the (composited) main window afterwards.
        byte[]? png = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var input = new MemoryStream(data);
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    input,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(decoder.Frames[0]));
                using var output = new MemoryStream();
                encoder.Save(output);
                png = output.ToArray();
            }
            catch
            {
                png = null;
            }
            finally
            {
                try { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { }
            }
        })
        { IsBackground = true, Name = "WIC decode" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)) || png is null)
            return null;

        try
        {
            using var ms = new MemoryStream(png);
            using Image image = Image.FromStream(ms);
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Scales an icon to a square thumbnail (high quality).</summary>
    public static Bitmap Thumbnail(Image source, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float scale = Math.Min((float)size / source.Width, (float)size / source.Height);
        float w = source.Width * scale, h = source.Height * scale;
        g.DrawImage(source, (size - w) / 2, (size - h) / 2, w, h);
        return bmp;
    }
}
