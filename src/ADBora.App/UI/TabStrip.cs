using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AdbTool.Core;

namespace AdbTool.UI;

internal enum TabGlyph { Settings, Backup, Install, Speed, Stream, Performance, Console, Info }

/// <summary>Tab bar of the main window: icon + text, accent line on the selected tab.</summary>
internal sealed class TabStrip : Border
{
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 6, 12, 0) };
    private readonly List<(Border Item, TabIcon Icon, TextBlock Text, Border Line)> _tabs = new();
    private int _selected = -1;

    public event Action<int>? SelectedIndexChanged;

    public TabStrip()
    {
        Background = AppTheme.HeaderBackground;
        BorderBrush = AppTheme.CardBorder;
        BorderThickness = new Thickness(0, 0, 0, 1);
        Child = new ScrollViewer
        {
            Content = _panel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    public void AddTab(TabGlyph glyph, string de, string en)
    {
        int index = _tabs.Count;
        var icon = new TabIcon { Glyph = glyph, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
        var text = new TextBlock { FontSize = AppTheme.TabSize, FontWeight = FontWeight.SemiBold };
        Ui.Bind(text, de, en);
        var line = new Border { Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 11, 16, 12), Children = { icon, text } };
        var item = new Border
        {
            CornerRadius = new CornerRadius(7, 7, 0, 0),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Panel { Children = { content, line } },
        };
        Loc.Bind(item, v => ToolTip.SetTip(item, v), de, en);
        item.PointerPressed += (_, e) => { if (e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) SelectedIndex = index; };
        item.PointerEntered += (_, _) => { if (index != _selected) item.Background = AppTheme.HeaderHover; };
        item.PointerExited += (_, _) => { if (index != _selected) item.Background = Brushes.Transparent; };
        _tabs.Add((item, icon, text, line));
        _panel.Children.Add(item);
        Paint();
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _tabs.Count || value == _selected) return;
            _selected = value;
            Paint();
            SelectedIndexChanged?.Invoke(value);
        }
    }

    private void Paint()
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            var (item, icon, text, line) = _tabs[i];
            bool selected = i == _selected;
            item.Background = selected ? AppTheme.Background : Brushes.Transparent;
            line.Background = selected ? AppTheme.Accent : Brushes.Transparent;
            text.Foreground = selected ? AppTheme.Text : AppTheme.Muted;
            icon.Stroke = selected ? AppTheme.Accent : AppTheme.Muted;
        }
    }
}

/// <summary>Small line icons for the tabs (vector, independent of icon fonts).</summary>
internal sealed class TabIcon : Control
{
    public TabGlyph Glyph { get; init; }

    private IBrush _stroke = Brushes.Gray;
    public IBrush Stroke { get => _stroke; set { _stroke = value; InvalidateVisual(); } }

    public override void Render(DrawingContext g)
    {
        var pen = new Pen(Stroke, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        double w = Bounds.Width, h = Bounds.Height, cx = w / 2, cy = h / 2;
        Point P(double x, double y) => new(x / 16 * w, y / 16 * h);
        void Line(double x1, double y1, double x2, double y2) => g.DrawLine(pen, P(x1, y1), P(x2, y2));
        void Poly(params double[] xy)
        {
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(P(xy[0], xy[1]), false);
                for (int i = 2; i < xy.Length; i += 2) c.LineTo(P(xy[i], xy[i + 1]));
                c.EndFigure(false);
            }
            g.DrawGeometry(null, pen, geo);
        }
        void Rect(double x, double y, double rw, double rh, double r = 1.5) =>
            g.DrawRectangle(null, pen, new Rect(P(x, y), P(x + rw, y + rh)), r, r);

        switch (Glyph)
        {
            case TabGlyph.Settings:
                g.DrawEllipse(null, pen, new Point(cx, cy), w * 0.17, h * 0.17);
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    Line(8 + 4.6 * Math.Cos(a), 8 + 4.6 * Math.Sin(a), 8 + 7 * Math.Cos(a), 8 + 7 * Math.Sin(a));
                }
                g.DrawEllipse(null, pen, new Point(cx, cy), w * 0.30, h * 0.30);
                break;
            case TabGlyph.Backup:          // archive box
                Rect(1.5, 2, 13, 3.5, 1);
                Poly(2.5, 5.5, 2.5, 14, 13.5, 14, 13.5, 5.5);
                Line(6, 8.5, 10, 8.5);
                break;
            case TabGlyph.Install:         // arrow into tray
                Line(8, 1.5, 8, 10);
                Poly(4.5, 6.5, 8, 10, 11.5, 6.5);
                Poly(1.5, 10.5, 1.5, 14.5, 14.5, 14.5, 14.5, 10.5);
                break;
            case TabGlyph.Speed:           // gauge
            {
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    c.BeginFigure(P(2, 12), false);
                    c.ArcTo(P(14, 12), new Size(6 / 16.0 * w, 6 / 16.0 * h), 0, true, SweepDirection.Clockwise);
                    c.EndFigure(false);
                }
                g.DrawGeometry(null, pen, geo);
                Line(8, 11, 11.5, 6);
                g.DrawEllipse(Stroke, null, P(8, 11), 1.3, 1.3);
                break;
            }
            case TabGlyph.Stream:          // screen with play symbol
                Rect(1.5, 2.5, 13, 9);
                Line(5.5, 14.5, 10.5, 14.5);
                Poly(6.5, 5, 10.5, 7, 6.5, 9, 6.5, 5);
                break;
            case TabGlyph.Performance:     // chart line
                Poly(1.5, 1.5, 1.5, 14.5, 14.5, 14.5);
                Poly(4, 11, 7, 7, 9.5, 9.5, 14, 3.5);
                break;
            case TabGlyph.Console:         // >_
                Rect(1.5, 2.5, 13, 11);
                Poly(4.5, 6, 7, 8, 4.5, 10);
                Line(8.5, 10.5, 11.5, 10.5);
                break;
            case TabGlyph.Info:
                g.DrawEllipse(null, pen, new Point(cx, cy), w * 0.42, h * 0.42);
                Line(8, 7.2, 8, 11.5);
                g.DrawEllipse(Stroke, null, P(8, 4.8), 1, 1);
                break;
        }
    }
}
