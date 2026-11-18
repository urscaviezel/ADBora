using System.Drawing.Drawing2D;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Cast;

/// <summary>
/// Window that shows the decoded casting picture (aspect-correct, black bars).
/// Double-click or F11 = full screen, Esc = leave full screen, F12 = screenshot.
/// Always shows the newest frame; older frames are dropped when the UI is busy.
/// </summary>
internal sealed class CastViewer : Form
{
    private readonly object _gate = new();
    private Bitmap? _current, _pending;
    private int _frames, _fps;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly string _title;
    private FormWindowState _oldState;
    private bool _fullScreen;

    /// <summary>Bitmaps no longer needed (for reuse by the decoder).</summary>
    public event Action<Bitmap>? Recycle;
    public event Action? ScreenshotRequested;
    public event Action? MuteToggleRequested;
    public event Action<int>? VolumeStepRequested;

    public CastViewer(string title)
    {
        _title = title;
        Text = title;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Theme.S(1100), Theme.S(640));
        MinimumSize = new Size(320, 200);
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        try { if (Icon.ExtractAssociatedIcon(Application.ExecutablePath) is { } icon) Icon = icon; } catch { }
        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);
        _timer.Tick += (_, _) =>
        {
            _fps = Interlocked.Exchange(ref _frames, 0);
            Bitmap? b = _current;
            Text = b is null ? _title + " – " + Loc.T("warte auf Bild …", "waiting for picture …") : $"{_title} – {b.Width} × {b.Height} · {_fps} fps";
        };
        _timer.Start();
        DoubleClick += (_, _) => ToggleFullScreen();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11) ToggleFullScreen();
            else if (e.KeyCode == Keys.Escape && _fullScreen) ToggleFullScreen();
            else if (e.KeyCode == Keys.F12) ScreenshotRequested?.Invoke();
            else if (e.KeyCode == Keys.M) MuteToggleRequested?.Invoke();
            else if (e.KeyCode is Keys.Add or Keys.Oemplus) VolumeStepRequested?.Invoke(+1);
            else if (e.KeyCode is Keys.Subtract or Keys.OemMinus) VolumeStepRequested?.Invoke(-1);
        };
    }

    /// <summary>Called from the decoding thread.</summary>
    public void Push(Bitmap frame)
    {
        Bitmap? dropped;
        lock (_gate) { dropped = _pending; _pending = frame; }
        if (dropped is not null) Recycle?.Invoke(dropped);
        Interlocked.Increment(ref _frames);
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Invalidate)); } catch { }
    }

    /// <summary>Copy of the newest picture (for screenshots).</summary>
    public Bitmap? Snapshot()
    {
        lock (_gate)
        {
            Bitmap? b = _pending ?? _current;
            return b is null ? null : new Bitmap(b);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Bitmap? old = null;
        lock (_gate)
        {
            if (_pending is not null) { old = _current; _current = _pending; _pending = null; }
            Graphics g = e.Graphics;
            g.Clear(Color.Black);
            if (_current is { } b)
            {
                float scale = Math.Min(ClientSize.Width / (float)b.Width, ClientSize.Height / (float)b.Height);
                int w = (int)(b.Width * scale), h = (int)(b.Height * scale);
                g.InterpolationMode = scale < 1 ? InterpolationMode.Bilinear : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(b, new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h));
            }
            else
            {
                TextRenderer.DrawText(g, Loc.T("Warte auf das Bild des Headsets …", "Waiting for the headset picture …"), Theme.BaseFont,
                    ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
        if (old is not null) Recycle?.Invoke(old);
    }

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _oldState = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            _fullScreen = true;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _oldState;
            _fullScreen = false;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        lock (_gate)
        {
            _current?.Dispose(); _pending?.Dispose();
            _current = _pending = null;
        }
        base.OnFormClosed(e);
    }
}
