using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using AdbTool.Cast;
using AdbTool.Core;

namespace AdbTool.UI;

/// <summary>
/// Window that shows the decoded casting picture (aspect-correct, black bars).
/// Double-click or F11 = full screen, Esc = leave full screen, F12 = screenshot, M = mute, +/- = volume.
/// Always shows the newest frame; older frames are dropped when the UI is busy.
/// </summary>
internal sealed class CastViewer : Window, ICastViewer
{
    private readonly object _gate = new();
    private SKBitmap? _current, _pending;
    private int _frames, _fps;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _title;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _waiting = new()
    {
        Foreground = Brushes.Gray, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };
    private WriteableBitmap? _surface;
    private bool _paintQueued;
    private volatile bool _closed;
    private WindowState _oldState;

    public event Action<SKBitmap>? Recycle;
    public event Action? ScreenshotRequested;
    public event Action? MuteToggleRequested;
    public event Action<int>? VolumeStepRequested;
    private Action? _closedHandlers;
    event Action? ICastViewer.Closed { add => _closedHandlers += value; remove => _closedHandlers -= value; }

    public bool IsClosed => _closed;

    public CastViewer(string title, bool onTop)
    {
        _title = title;
        Title = title;
        Icon = MainWindow.AppIcon;
        Background = Brushes.Black;
        Width = 1100;
        Height = 640;
        MinWidth = 320;
        MinHeight = 200;
        Topmost = onTop;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.MediumQuality);
        _waiting.Text = Loc.T("Warte auf das Bild des Headsets …", "Waiting for the headset picture …");
        Content = new Panel { Children = { _waiting, _image } };

        _timer.Tick += (_, _) =>
        {
            _fps = Interlocked.Exchange(ref _frames, 0);
            SKBitmap? b = _current;
            Title = b is null ? _title + " – " + Loc.T("warte auf Bild …", "waiting for picture …") : $"{_title} – {b.Width} × {b.Height} · {_fps} fps";
        };
        _timer.Start();
        DoubleTapped += (_, _) => ToggleFullScreen();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) ToggleFullScreen();
            else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen) ToggleFullScreen();
            else if (e.Key == Key.F12) ScreenshotRequested?.Invoke();
            else if (e.Key == Key.M) MuteToggleRequested?.Invoke();
            else if (e.Key is Key.Add or Key.OemPlus) VolumeStepRequested?.Invoke(+1);
            else if (e.Key is Key.Subtract or Key.OemMinus) VolumeStepRequested?.Invoke(-1);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            lock (_gate)
            {
                _current?.Dispose(); _pending?.Dispose();
                _current = _pending = null;
            }
            _closedHandlers?.Invoke();
        };
    }

    /// <summary>Called from the decoding thread.</summary>
    public void Push(SKBitmap frame)
    {
        if (_closed) { frame.Dispose(); return; }
        SKBitmap? dropped;
        bool queue;
        lock (_gate)
        {
            dropped = _pending;
            _pending = frame;
            queue = !_paintQueued;
            _paintQueued = true;
        }
        if (dropped is not null) Recycle?.Invoke(dropped);
        Interlocked.Increment(ref _frames);
        if (queue) Dispatcher.UIThread.Post(ShowPending, DispatcherPriority.Render);
    }

    /// <summary>UI thread: copies the newest frame into the on-screen bitmap.</summary>
    private unsafe void ShowPending()
    {
        SKBitmap? old = null;
        lock (_gate)
        {
            _paintQueued = false;
            if (_pending is null || _closed) return;
            old = _current;
            _current = _pending;
            _pending = null;

            SKBitmap b = _current;
            if (_surface is null || _surface.PixelSize.Width != b.Width || _surface.PixelSize.Height != b.Height)
            {
                _surface?.Dispose();
                _surface = new WriteableBitmap(new PixelSize(b.Width, b.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _image.Source = _surface;
                _waiting.IsVisible = false;
            }
            using (ILockedFramebuffer fb = _surface.Lock())
            {
                byte* src = (byte*)b.GetPixels();
                byte* dst = (byte*)fb.Address;
                int rowBytes = Math.Min(b.RowBytes, fb.RowBytes);
                if (b.RowBytes == fb.RowBytes)
                    Buffer.MemoryCopy(src, dst, (long)fb.RowBytes * b.Height, (long)b.RowBytes * b.Height);
                else
                    for (int y = 0; y < b.Height; y++)
                        Buffer.MemoryCopy(src + (long)y * b.RowBytes, dst + (long)y * fb.RowBytes, fb.RowBytes, rowBytes);
            }
        }
        if (old is not null) Recycle?.Invoke(old);
        _image.InvalidateVisual();
    }

    /// <summary>Copy of the newest picture (for screenshots).</summary>
    public SKBitmap? Snapshot()
    {
        lock (_gate)
        {
            SKBitmap? b = _pending ?? _current;
            return b?.Copy();
        }
    }

    void ICastViewer.Show() => Dispatcher.UIThread.Post(Show);

    void ICastViewer.Close() => Dispatcher.UIThread.Post(() => { if (!_closed) Close(); });

    private void ToggleFullScreen()
    {
        if (WindowState != WindowState.FullScreen)
        {
            _oldState = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else
        {
            WindowState = _oldState;
        }
    }
}
