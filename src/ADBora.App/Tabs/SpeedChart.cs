using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>One throughput sample (every 250 ms): seconds since test start, Mbit/s, phase index.</summary>
internal readonly record struct RateSample(double T, double Mbps, int Phase);

/// <summary>Wi-Fi state of the headset at a point in time (every ~2 s, from "cmd wifi status").</summary>
internal readonly record struct WifiSample(double T, int Rssi, int TxLink, int RxLink, int Frequency, double RetryPercent, long LostDelta);

/// <summary>Dips (below half the median of the phase) and stalls (practically 0) of a test.
/// Down / Up: separate values per direction (TCP/IP direct test measures both).</summary>
internal sealed record SpeedAnalysis(double Median, double Average, double Min, int Dips, int Stalls, double LongestStall, double StallSeconds,
                                     SpeedAnalysis? Down = null, SpeedAnalysis? Up = null)
{
    /// <summary>Summary in the current UI language (re-evaluated when the language changes); per direction if measured both ways.</summary>
    public string Summary() => Down is { } d && Up is { } u
        ? $"↓ {d.Summary()}   |   ↑ {u.Summary()}"
        : Values();

    private string Values() =>
        Loc.T($"Median {Median:0} Mbit/s · Ø {Average:0} · Min {Min:0} · {Dips} Einbruch/Einbrüche unter 50 % · {Stalls} Aussetzer",
              $"Median {Median:0} Mbit/s · avg {Average:0} · min {Min:0} · {Dips} dip(s) below 50 % · {Stalls} stall(s)")
        + (Stalls > 0 ? Loc.T($", längster {LongestStall:0.0} s, gesamt {StallSeconds:0.0} s", $", longest {LongestStall:0.0} s, total {StallSeconds:0.0} s") : "");
}

/// <summary>
/// Throughput history of a speed test: rate per 250 ms (colour per phase/direction),
/// dips and stalls highlighted, below that the headset's Wi-Fi signal (RSSI) and packet
/// retry rate. Can be exported as PNG report and CSV.
/// </summary>
internal sealed class SpeedChart : Control
{
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    public static readonly IBrush[] PhaseColors = { B("#67d5b5"), B("#6fa8ff"), B("#e07bb5"), B("#e0b060") };
    private static readonly IBrush RssiColor = B("#e0b060");
    private static readonly IBrush RetryColor = B("#ef7b7b");

    private readonly List<RateSample> _samples = new();
    private readonly List<WifiSample> _wifi = new();
    private readonly List<Func<string>> _phases = new(); // names in the current UI language
    private readonly List<int> _phaseDirection = new();  // 0 = one direction only, 1 = down (headset → PC), 2 = up
    private readonly object _gate = new();

    public bool HasData { get { lock (_gate) return _samples.Count > 1; } }

    public void Clear()
    {
        lock (_gate) { _samples.Clear(); _wifi.Clear(); _phases.Clear(); _phaseDirection.Clear(); }
        Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    /// <summary>Starts a new phase (e.g. "↓ headset → PC") and returns its index.</summary>
    public int AddPhase(string name) => AddPhase(() => name);

    /// <param name="direction">0 = single-direction test, 1 = down (headset → PC), 2 = up (PC → headset)</param>
    public int AddPhase(Func<string> name, int direction = 0)
    {
        lock (_gate) { _phases.Add(name); _phaseDirection.Add(direction); return _phases.Count - 1; }
    }

    public void Add(RateSample s)
    {
        lock (_gate) _samples.Add(s);
        Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    public void Add(WifiSample w)
    {
        lock (_gate) _wifi.Add(w);
        Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    /// <summary>Dips/stalls per phase; the first second of each phase (ramp-up) is ignored.</summary>
    public SpeedAnalysis? Analyze()
    {
        lock (_gate)
        {
            SpeedAnalysis? all = Analyze(_samples);
            if (all is null) return null;
            List<RateSample> Dir(int d) => _samples.Where(s => s.Phase < _phaseDirection.Count && _phaseDirection[s.Phase] == d).ToList();
            SpeedAnalysis? down = Analyze(Dir(1)), up = Analyze(Dir(2));
            return down is not null && up is not null ? all with { Down = down, Up = up } : all;
        }
    }

    private static SpeedAnalysis? Analyze(List<RateSample> samples)
    {
        if (samples.Count < 4) return null;
        int dips = 0, stalls = 0;
        double longest = 0, stallTotal = 0;
        var all = new List<double>();
        foreach (var phase in samples.GroupBy(s => s.Phase))
        {
            var list = phase.OrderBy(s => s.T).ToList();
            double start = list[0].T;
            var steady = list.Where(s => s.T - start >= 1.0).ToList();
            if (steady.Count < 2) continue;
            double median = Median(steady.Select(s => s.Mbps));
            all.AddRange(steady.Select(s => s.Mbps));
            bool inDip = false, inStall = false;
            double stallStart = 0, prevT = steady[0].T;
            foreach (var s in steady)
            {
                bool stall = s.Mbps < Math.Max(1.0, median * 0.02);
                bool dip = !stall && s.Mbps < median * 0.5;
                if (stall && !inStall) { stalls++; stallStart = prevT; }
                if (!stall && inStall)
                {
                    double d = s.T - stallStart;
                    longest = Math.Max(longest, d);
                    stallTotal += d;
                }
                if (dip && !inDip) dips++;
                inStall = stall;
                inDip = dip;
                prevT = s.T;
            }
            if (inStall)
            {
                double d = steady[^1].T - stallStart;
                longest = Math.Max(longest, d);
                stallTotal += d;
            }
        }
        if (all.Count == 0) return null;
        return new SpeedAnalysis(Median(all), all.Average(), all.Min(), dips, stalls, longest, stallTotal);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] v = values.OrderBy(x => x).ToArray();
        return v.Length == 0 ? 0 : v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    public override void Render(DrawingContext g) => Draw(g, new Rect(Bounds.Size), 1, AppTheme.BaseSize - 1);

    private static FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);

    /// <summary>Draws the chart (also used for the PNG report).</summary>
    private void Draw(DrawingContext g, Rect area, double k, double fontSize)
    {
        g.FillRectangle(AppTheme.Card, area);
        RateSample[] samples;
        WifiSample[] wifi;
        string[] phases;
        lock (_gate) { samples = _samples.ToArray(); wifi = _wifi.ToArray(); phases = _phases.Select(p => p()).ToArray(); }
        void Label(string s, double x, double y, IBrush brush, bool right = false, double width = 0)
        {
            FormattedText t = Text(s, fontSize, brush);
            g.DrawText(t, new Point(right ? x + width - t.Width : x, y));
        }
        if (samples.Length < 2)
        {
            FormattedText t = Text(Loc.T("Hier erscheint der Verlauf der Übertragungsrate während eines Tests.", "The transfer rate history appears here during a test."), fontSize, AppTheme.Muted);
            g.DrawText(t, new Point(area.X + (area.Width - t.Width) / 2, area.Y + (area.Height - t.Height) / 2));
            return;
        }
        double axisW = 64 * k, legendH = 22 * k;
        bool hasWifi = wifi.Length > 0;
        double wifiH = hasWifi ? area.Height * 0.3 : 0;
        var rate = new Rect(area.Left + axisW, area.Top + legendH, area.Width - axisW - 56 * k, area.Height - legendH - wifiH - 22 * k - (hasWifi ? 10 * k : 0));
        if (rate.Width < 40 || rate.Height < 30) return;
        double tMax = Math.Max(samples[^1].T, wifi.Length > 0 ? wifi[^1].T : 0);
        tMax = Math.Max(tMax, 1);
        double X(double t) => rate.Left + t / tMax * rate.Width;

        // Legend: phases
        double lx = area.Left + axisW;
        for (int i = 0; i < phases.Length; i++)
        {
            g.FillRectangle(PhaseColors[i % PhaseColors.Length], new Rect(lx, area.Top + 7 * k, 14 * k, 4 * k));
            FormattedText t = Text(phases[i], fontSize, AppTheme.Text);
            g.DrawText(t, new Point(lx + 18 * k, area.Top));
            lx += t.Width + 52 * k;
        }
        if (hasWifi)
        {
            foreach ((string name, IBrush c) in new[] { ("RSSI (dBm)", RssiColor), (Loc.T("Paketwiederholungen (%)", "Packet retries (%)"), RetryColor) })
            {
                g.FillRectangle(c, new Rect(lx, area.Top + 7 * k, 14 * k, 4 * k));
                FormattedText t = Text(name, fontSize, AppTheme.Muted);
                g.DrawText(t, new Point(lx + 18 * k, area.Top));
                lx += t.Width + 52 * k;
            }
        }

        // Rate plot
        g.FillRectangle(AppTheme.Console, rate);
        double max = Math.Max(10, samples.Max(s => s.Mbps)) * 1.1;
        double step = NiceStep(max / 4);
        var gridPen = new Pen(AppTheme.Grid, 1);
        for (double v = 0; v <= max; v += step)
        {
            double y = rate.Bottom - v / max * rate.Height;
            g.DrawLine(gridPen, new Point(rate.Left, y), new Point(rate.Right, y));
            Label(v.ToString("0", CultureInfo.InvariantCulture), area.Left, y - 9 * k, AppTheme.DisabledText, right: true, width: axisW - 6 * k);
        }
        Label("Mbit/s", area.Left, rate.Top - 18 * k, AppTheme.DisabledText);

        // Dips / stalls as coloured bands (per phase median)
        IBrush stallBand = new SolidColorBrush(Color.FromArgb(90, 0xef, 0x7b, 0x7b));
        IBrush dipBand = new SolidColorBrush(Color.FromArgb(45, 0xe0, 0xb0, 0x60));
        foreach (var phase in samples.GroupBy(s => s.Phase))
        {
            var list = phase.OrderBy(s => s.T).ToList();
            var steady = list.Where(s => s.T - list[0].T >= 1.0).ToList();
            if (steady.Count < 2) continue;
            double median = Median(steady.Select(s => s.Mbps));
            for (int i = 1; i < steady.Count; i++)
            {
                var s = steady[i];
                bool stall = s.Mbps < Math.Max(1.0, median * 0.02);
                bool dip = !stall && s.Mbps < median * 0.5;
                if (!stall && !dip) continue;
                double x1 = X(steady[i - 1].T), x2 = X(s.T);
                g.FillRectangle(stall ? stallBand : dipBand, new Rect(x1, rate.Top, Math.Max(1, x2 - x1), rate.Height));
            }
        }

        // Rate lines
        foreach (var phase in samples.GroupBy(s => s.Phase))
        {
            var pen = new Pen(PhaseColors[phase.Key % PhaseColors.Length], 1.6 * k, lineJoin: PenLineJoin.Round);
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                bool first = true;
                foreach (var s in phase.OrderBy(s => s.T))
                {
                    var p = new Point(X(s.T), rate.Bottom - Math.Min(s.Mbps, max) / max * rate.Height);
                    if (first) { c.BeginFigure(p, false); first = false; } else c.LineTo(p);
                }
                if (!first) c.EndFigure(false);
            }
            g.DrawGeometry(null, pen, geo);
        }

        // Time axis
        double tStep = NiceStep(tMax / 8);
        for (double t = 0; t <= tMax + 1e-6; t += tStep)
        {
            double x = X(t);
            string label = t >= 120 ? $"{(int)t / 60}:{(int)t % 60:00}" : $"{t:0} s";
            Label(label, x - 14 * k, (hasWifi ? rate.Bottom : area.Bottom - 20 * k) + 2 * k, AppTheme.DisabledText);
        }

        // Wi-Fi panel: RSSI (left scale -90…-20 dBm) and retry rate (right scale 0…max %)
        if (hasWifi)
        {
            var wr = new Rect(rate.Left, rate.Bottom + 22 * k, rate.Width, wifiH - 4 * k);
            if (wr.Height < 20) return;
            g.FillRectangle(AppTheme.Console, wr);
            g.DrawLine(gridPen, new Point(wr.Left, wr.Top + wr.Height / 2), new Point(wr.Right, wr.Top + wr.Height / 2));
            const double rMin = -90, rMax = -20;
            double retryMax = Math.Max(10, wifi.Max(w => w.RetryPercent) * 1.1);
            Label("-20", area.Left, wr.Top - 2 * k, RssiColor, right: true, width: axisW - 6 * k);
            Label("-90", area.Left, wr.Bottom - 16 * k, RssiColor, right: true, width: axisW - 6 * k);
            Label($"{retryMax:0} %", wr.Right + 4 * k, wr.Top - 2 * k, RetryColor);
            Label("0 %", wr.Right + 4 * k, wr.Bottom - 16 * k, RetryColor);
            void Line(Func<WifiSample, double> value, double lo, double hi, IBrush color)
            {
                var pen = new Pen(color, 1.6 * k, lineJoin: PenLineJoin.Round);
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    bool first = true;
                    foreach (var w in wifi)
                    {
                        double v = Math.Clamp(value(w), lo, hi);
                        var p = new Point(X(w.T), wr.Bottom - (v - lo) / (hi - lo) * wr.Height);
                        if (first) { c.BeginFigure(p, false); first = false; } else c.LineTo(p);
                    }
                    if (!first) c.EndFigure(false);
                }
                g.DrawGeometry(null, pen, geo);
            }
            Line(w => w.Rssi, rMin, rMax, RssiColor);
            Line(w => w.RetryPercent, 0, retryMax, RetryColor);
            var last = wifi[^1];
            Label($"{last.Rssi} dBm · {last.Frequency} MHz · Tx {last.TxLink} / Rx {last.RxLink} Mbit/s", wr.Left + 6 * k, wr.Top + 2 * k, AppTheme.Muted);
        }
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag;
    }

    /// <summary>Saves PNG (chart + header with device, firmware, target, summary) and CSV with all samples. UI thread.</summary>
    public (string Png, string Csv) Export(string folder, string baseName, IReadOnlyList<string> header)
    {
        Directory.CreateDirectory(folder);
        string png = Path.Combine(folder, baseName + ".png"), csv = Path.Combine(folder, baseName + ".csv");
        const int w = 1800, h = 900;
        using (var bmp = new RenderTargetBitmap(new PixelSize(w, h)))
        {
            using (DrawingContext g = bmp.CreateDrawingContext())
            {
                g.FillRectangle(AppTheme.Card, new Rect(0, 0, w, h));
                double y = 18;
                g.DrawText(Text(header.Count > 0 ? header[0] : "ADBora", 20, AppTheme.Text, FontWeight.SemiBold), new Point(24, y));
                y += 36;
                foreach (string line in header.Skip(1))
                {
                    g.DrawText(Text(line, 15, AppTheme.Muted), new Point(24, y));
                    y += 24;
                }
                Draw(g, new Rect(16, y + 10, w - 32, h - y - 40), 1.6, 15);
                FormattedText footer = Text("ADBora " + AppInfo.Version + " · github.com/urscaviezel/ADBora", 14, AppTheme.DisabledText);
                g.DrawText(footer, new Point(w - 20 - footer.Width, h - 26));
            }
#pragma warning disable CS0618 // PNG with default options
            bmp.Save(png);
#pragma warning restore CS0618
        }

        RateSample[] samples;
        WifiSample[] wifi;
        string[] phases;
        lock (_gate) { samples = _samples.ToArray(); wifi = _wifi.ToArray(); phases = _phases.Select(p => p()).ToArray(); }
        var sb = new StringBuilder();
        foreach (string line in header) sb.Append("# ").AppendLine(line);
        sb.AppendLine("time_s;phase;mbit_s;rssi_dbm;tx_link_mbit;rx_link_mbit;frequency_mhz;retry_percent;lost_tx_packets");
        var inv = CultureInfo.InvariantCulture;
        int wi = -1;
        foreach (var s in samples)
        {
            while (wi + 1 < wifi.Length && wifi[wi + 1].T <= s.T) wi++;
            string wcols = wi >= 0
                ? string.Join(";", wifi[wi].Rssi, wifi[wi].TxLink, wifi[wi].RxLink, wifi[wi].Frequency, wifi[wi].RetryPercent.ToString("0.00", inv), wifi[wi].LostDelta)
                : ";;;;;";
            sb.Append(s.T.ToString("0.00", inv)).Append(';').Append(s.Phase < phases.Length ? phases[s.Phase] : "").Append(';')
              .Append(s.Mbps.ToString("0.0", inv)).Append(';').AppendLine(wcols);
        }
        File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(true));
        return (png, csv);
    }
}
