using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>Which values are shown as tiles and which get a graph.</summary>
internal static class MetricSelectDialog
{
    public static async Task<bool> Show(Window owner, HashSet<string> hidden, List<string> graphs, int maxGraphs, IReadOnlyDictionary<string, double> current,
                                        string? deviceKind = null, bool pfdMetricsInstalled = false)
    {
        bool measuring = current.Count > 0;
        bool Has(string k) => current.TryGetValue(k, out double v) && !double.IsNaN(v);
        var show = PerfMetrics.All.ToDictionary(m => m.Key, m => !hidden.Contains(m.Key));
        var graph = PerfMetrics.All.ToDictionary(m => m.Key, m => graphs.Contains(m.Key));
        var order = new List<string>(graphs);

        Window dialog = Dialogs.Dialog(Loc.T("Werte auswählen", "Choose values"), 780, 700);
        var info = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Muted, Margin = new Thickness(0, 0, 0, 10),
            Text = Loc.T($"„Anzeigen“ = Kachel im Tab, „Graph“ = Verlauf im Diagramm (höchstens {maxGraphs}). Nicht jedes Headset liefert jeden Wert – "
                         + (measuring ? "„Aktuell“ zeigt, was das Gerät gerade liefert (– = nicht verfügbar)." : "starte eine Messung, dann zeigt „Aktuell“, was das Gerät liefert.")
                         + " Akku-Strom und Leistungsaufnahme (W) beim Play For Dream kommen von PFD-Metrics – dort muss die Messung laufen. Spannung, Strom und Leistung stimmen nur im Standalone-Betrieb (ADB über WLAN); über USB verfälscht das Laden die Werte.",
                         $"\"Show\" = tile in the tab, \"Graph\" = history in the chart (at most {maxGraphs}). Not every headset delivers every value – "
                         + (measuring ? "\"Current\" shows what the device delivers right now (– = not available)." : "start monitoring and \"Current\" shows what the device delivers.")
                         + " Battery current and power draw (W) on the Play For Dream come from PFD-Metrics – its monitoring must be running. Voltage, current and power are only correct in standalone use (ADB over Wi-Fi); over USB, charging distorts them.")
        };

        const string columns = "*,170,90,90";
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Background = AppTheme.TableHeader, MinHeight = 34 };
        string[] titles = { Loc.T("Wert", "Value"), Loc.T("Aktuell", "Current"), Loc.T("Anzeigen", "Show"), "Graph" };
        for (int c = 0; c < titles.Length; c++)
        {
            var t = new TextBlock { Text = titles[c], FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 0), HorizontalAlignment = c >= 2 ? HorizontalAlignment.Center : HorizontalAlignment.Left };
            Grid.SetColumn(t, c);
            header.Children.Add(t);
        }
        var rows = new StackPanel();
        var boxes = new List<(string Key, CheckBox Show, CheckBox Graph)>();

        string GroupText(MetricGroup g) => g switch
        {
            MetricGroup.Runtime => "XR-Runtime",
            MetricGroup.Compositor => "Compositor",
            _ => "System"
        };
        int i = 0;
        bool syncing = false;
        void Sync()
        {
            syncing = true;
            foreach (var (k, s, gr) in boxes) { s.IsChecked = show[k]; gr.IsChecked = graph[k]; }
            syncing = false;
        }
        foreach (PerfMetric m in PerfMetrics.All)
        {
            if (deviceKind == "pfd" && PerfMetrics.PfdUnavailable.Contains(m.Key) && !Has(m.Key)) continue;
            string why = deviceKind switch
            {
                "pfd" when PerfMetrics.QuestOnly.Contains(m.Key) => Loc.T("nur Quest", "Quest only"),
                "pfd" when PerfMetrics.PfdMetricsOnly.Contains(m.Key) => pfdMetricsInstalled ? Loc.T("– (PFD-Metrics-Messung?)", "– (PFD-Metrics running?)") : Loc.T("nur mit PFD-Metrics", "PFD-Metrics only"),
                _ => "–"
            };
            string now = Has(m.Key) ? m.Format(current[m.Key]) + (m.Unit.Length > 0 ? " " + m.Unit : "") : (measuring || deviceKind == "pfd" && why != "–" ? why : "");
            IBrush fore = measuring && !Has(m.Key) ? AppTheme.Muted : AppTheme.Text;
            string key = m.Key;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), MinHeight = 30, Background = i++ % 2 == 1 ? AppTheme.TableAlt : AppTheme.Card };
            var name = new TextBlock { Text = $"{GroupText(m.Group)} · {m.Label}{(m.Unit.Length > 0 ? $" ({m.Unit})" : "")}", Foreground = fore, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var value = new TextBlock { Text = now, Foreground = fore, Margin = new Thickness(8, 0) };
            var s = new CheckBox { IsChecked = show[key], HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 0, Padding = default };
            var gr = new CheckBox { IsChecked = graph[key], HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 0, Padding = default };
            s.IsCheckedChanged += (_, _) => { if (!syncing) show[key] = s.IsChecked == true; };
            gr.IsCheckedChanged += (_, _) =>
            {
                if (syncing) return;
                if (gr.IsChecked != true) { graph[key] = false; order.Remove(key); }
                else
                {
                    if (order.Count >= maxGraphs) { graph[order[0]] = false; order.RemoveAt(0); }
                    graph[key] = true;
                    order.Add(key);
                }
                Sync();
            };
            Grid.SetColumn(value, 1); Grid.SetColumn(s, 2); Grid.SetColumn(gr, 3);
            grid.Children.Add(name); grid.Children.Add(value); grid.Children.Add(s); grid.Children.Add(gr);
            rows.Children.Add(new Border { BorderBrush = AppTheme.Grid, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid });
            boxes.Add((key, s, gr));
        }

        var ok = Ui.Button("Übernehmen", "Apply", ButtonKind.Primary);
        var cancel = Ui.Button("Abbrechen", "Cancel");
        var all = measuring ? Ui.Button("Alle verfügbaren", "All available") : Ui.Button("Alle anzeigen", "Show all");
        var defaults = Ui.Button("Standard", "Defaults");
        bool accepted = false;
        ok.IsDefault = true;
        cancel.IsCancel = true;
        cancel.Margin = default;
        ok.Click += (_, _) => { accepted = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        all.Click += (_, _) => { foreach (string k in show.Keys.ToList()) show[k] = measuring ? Has(k) : !(deviceKind == "pfd" && PerfMetrics.QuestOnly.Contains(k)); Sync(); };
        defaults.Click += (_, _) =>
        {
            foreach (string k in show.Keys.ToList()) show[k] = PerfMetrics.DefaultShown.Contains(k);
            order = PerfMetrics.DefaultGraphs.ToList();
            foreach (string k in graph.Keys.ToList()) graph[k] = order.Contains(k);
            Sync();
        };
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { ok, cancel } };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { all, defaults } });

        var table = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        table.Children.Add(new ScrollViewer { Content = rows });
        var body = new DockPanel { Margin = new Thickness(22) };
        DockPanel.SetDock(info, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(info);
        body.Children.Add(buttons);
        body.Children.Add(new Border { BorderBrush = AppTheme.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = AppTheme.Card, Child = table });
        dialog.Content = body;

        await dialog.ShowDialog(owner);
        if (!accepted) return false;
        hidden.Clear();
        foreach (var (k, v) in show) if (!v) hidden.Add(k);
        graphs.Clear();
        graphs.AddRange(order.Where(k => graph[k]));
        return true;
    }
}

/// <summary>Small value tile (label, value, unit); click = toggle graph.</summary>
internal sealed class MetricTile : Border
{
    private readonly PerfMetric _metric;
    private readonly TextBlock _value = new() { FontSize = 19, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private string _text = "–";
    private bool _selected;

    public event Action? Click;

    public MetricTile(PerfMetric metric)
    {
        _metric = metric;
        Width = 176;
        Height = 66;
        Margin = new Thickness(0, 0, 8, 8);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(10, 6);
        Cursor = new Cursor(StandardCursorType.Hand);
        Background = AppTheme.Card;
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = metric.Label, Foreground = AppTheme.Muted, TextTrimming = TextTrimming.CharacterEllipsis },
                _value,
            }
        };
        PointerEntered += (_, _) => Background = AppTheme.ButtonHover;
        PointerExited += (_, _) => Background = AppTheme.Card;
        PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Click?.Invoke(); };
        Paint();
    }

    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Paint(); } }
    }

    public void SetValue(double v)
    {
        string text = _metric.Format(v);
        if (text == _text) return;
        _text = text;
        Paint();
    }

    private void Paint()
    {
        BorderBrush = _selected ? AppTheme.Accent : AppTheme.CardBorder;
        BorderThickness = new Thickness(_selected ? 2 : 1);
        _value.Text = _text + (_metric.Unit.Length > 0 && _text != "–" ? " " + _metric.Unit : "");
        _value.Foreground = _text == "–" ? AppTheme.DisabledText : AppTheme.Text;
    }
}

/// <summary>Stacked line charts (one per metric) with current/min/avg/max.</summary>
internal sealed class MetricChart : Control
{
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static readonly IBrush[] Colors = { B("#67d5b5"), B("#6fa8ff"), B("#e0b060"), B("#e07bb5") };
    private const int Window = 120;
    private List<(PerfMetric Metric, double[] Values)> _data = new();

    public void SetData(List<(PerfMetric, double[])> data)
    {
        _data = data;
        InvalidateVisual();
    }

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal, double maxWidth = 0)
    {
        var t = new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);
        if (maxWidth > 0) { t.MaxTextWidth = maxWidth; t.MaxLineCount = 1; t.Trimming = TextTrimming.CharacterEllipsis; }
        return t;
    }

    public override void Render(DrawingContext g)
    {
        var bounds = new Rect(Bounds.Size);
        g.FillRectangle(AppTheme.Card, bounds);
        if (_data.Count == 0)
        {
            FormattedText t = Text(Loc.T("Kachel anklicken, um sie hier als Verlauf anzuzeigen.", "Click a tile to show its history here."), AppTheme.BaseSize, AppTheme.Muted);
            g.DrawText(t, new Point((bounds.Width - t.Width) / 2, (bounds.Height - t.Height) / 2));
            return;
        }
        int n = _data.Count;
        double gap = 8;
        double rowH = (bounds.Height - gap * (n - 1)) / n;
        double labelW = 200;
        var gridPen = new Pen(AppTheme.Grid, 1);
        for (int i = 0; i < n; i++)
        {
            var (metric, all) = _data[i];
            IBrush color = Colors[i % Colors.Length];
            double top = i * (rowH + gap);
            double[] values = all.Length > Window ? all[^Window..] : all;
            double[] valid = values.Where(v => !double.IsNaN(v)).ToArray();

            // Legend
            string cur = valid.Length > 0 && !double.IsNaN(values[^1]) ? metric.Format(values[^1]) : "–";
            string unit = metric.Unit.Length > 0 ? " " + metric.Unit : "";
            g.DrawText(Text(metric.Label, AppTheme.BaseSize, color, FontWeight.SemiBold, labelW), new Point(0, top));
            g.DrawText(Text(cur + unit, AppTheme.SectionSize, AppTheme.Text, FontWeight.SemiBold, labelW), new Point(0, top + 20));
            if (valid.Length > 0)
                g.DrawText(Text(Loc.T($"Min {metric.Format(valid.Min())} · Ø {metric.Format(valid.Average())} · Max {metric.Format(valid.Max())}",
                                      $"Min {metric.Format(valid.Min())} · avg {metric.Format(valid.Average())} · Max {metric.Format(valid.Max())}"),
                    AppTheme.BaseSize - 1, AppTheme.Muted, FontWeight.Normal, labelW), new Point(0, top + 46));

            // Plot area
            var plot = new Rect(labelW + 8, top + 2, bounds.Width - labelW - 12, rowH - 4);
            if (plot.Width < 20 || plot.Height < 10) continue;
            g.FillRectangle(AppTheme.Console, plot);
            double max = Math.Max(metric.GraphMax, valid.Length > 0 ? valid.Max() * 1.1 : 0);
            double min = Math.Min(0, valid.Length > 0 ? valid.Min() : 0);
            for (int k = 1; k < 4; k++)
            {
                double y = plot.Top + plot.Height * k / 4;
                g.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            }
            g.DrawText(Text(metric.Format(max), AppTheme.BaseSize - 1, AppTheme.DisabledText), new Point(plot.Right - 60, plot.Top));
            if (values.Length < 2) continue;
            var pen = new Pen(color, 1.8, lineJoin: PenLineJoin.Round);
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                bool open = false;
                for (int k = 0; k < values.Length; k++)
                {
                    double v = values[k];
                    if (double.IsNaN(v)) { if (open) { c.EndFigure(false); open = false; } continue; }
                    double x = plot.Right - (double)(values.Length - 1 - k) / (Window - 1) * plot.Width;
                    double y = plot.Bottom - (v - min) / Math.Max(1e-9, max - min) * plot.Height;
                    var p = new Point(x, Math.Clamp(y, plot.Top, plot.Bottom));
                    if (!open) { c.BeginFigure(p, false); open = true; } else c.LineTo(p);
                }
                if (open) c.EndFigure(false);
            }
            g.DrawGeometry(null, pen, geo);
        }
    }
}
