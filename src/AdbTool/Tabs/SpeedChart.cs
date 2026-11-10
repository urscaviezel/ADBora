using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>One throughput sample (every 250 ms): seconds since test start, Mbit/s, phase index.</summary>
internal readonly record struct RateSample(double T, double Mbps, int Phase);

/// <summary>Wi-Fi state of the headset at a point in time (every ~2 s, from "cmd wifi status").</summary>
internal readonly record struct WifiSample(double T, int Rssi, int TxLink, int RxLink, int Frequency, double RetryPercent, long LostDelta);

/// <summary>Dips (below half the median of the phase) and stalls (practically 0) of a test.</summary>
internal sealed record SpeedAnalysis(double Median, double Average, double Min, int Dips, int Stalls, double LongestStall, double StallSeconds)
{
    public string Summary(bool english) => english
        ? $"Median {Median:0} Mbit/s · avg {Average:0} · min {Min:0} · {Dips} dip(s) below 50 % · {Stalls} stall(s)" + (Stalls > 0 ? $", longest {LongestStall:0.0} s, total {StallSeconds:0.0} s" : "")
        : $"Median {Median:0} Mbit/s · Ø {Average:0} · Min {Min:0} · {Dips} Einbruch/Einbrüche unter 50 % · {Stalls} Aussetzer" + (Stalls > 0 ? $", längster {LongestStall:0.0} s, gesamt {StallSeconds:0.0} s" : "");
}

/// <summary>
/// Throughput history of a speed test: rate per 250 ms (colour per phase/direction),
/// dips and stalls highlighted, below that the headset's Wi-Fi signal (RSSI) and packet
/// retry rate. Can be exported as PNG report and CSV.
/// </summary>
internal sealed class SpeedChart : Control
{
    public static readonly Color[] PhaseColors = { ColorTranslator.FromHtml("#67d5b5"), ColorTranslator.FromHtml("#6fa8ff"), ColorTranslator.FromHtml("#e07bb5"), ColorTranslator.FromHtml("#e0b060") };
    private static readonly Color RssiColor = ColorTranslator.FromHtml("#e0b060");
    private static readonly Color RetryColor = ColorTranslator.FromHtml("#ef7b7b");

    private readonly List<RateSample> _samples = new();
    private readonly List<WifiSample> _wifi = new();
    private readonly List<string> _phases = new();
    private readonly object _gate = new();

    public SpeedChart()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Font = Theme.BaseFont;
    }

    public bool HasData { get { lock (_gate) return _samples.Count > 1; } }

    public void Clear()
    {
        lock (_gate) { _samples.Clear(); _wifi.Clear(); _phases.Clear(); }
        Invalidate();
    }

    /// <summary>Starts a new phase (e.g. "↓ headset → PC") and returns its index.</summary>
    public int AddPhase(string name)
    {
        lock (_gate) { _phases.Add(name); return _phases.Count - 1; }
    }

    public void Add(RateSample s)
    {
        lock (_gate) _samples.Add(s);
        Invalidate();
    }

    public void Add(WifiSample w)
    {
        lock (_gate) _wifi.Add(w);
        Invalidate();
    }

    /// <summary>Dips/stalls per phase; the first second of each phase (ramp-up) is ignored.</summary>
    public SpeedAnalysis? Analyze()
    {
        lock (_gate) return Analyze(_samples);
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

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics, ClientRectangle, Theme.S(1f));

    /// <summary>Draws the chart (also used for the PNG report).</summary>
    private void Draw(Graphics g, Rectangle area, float k)
    {
        g.Clear(Theme.Card);
        RateSample[] samples;
        WifiSample[] wifi;
        string[] phases;
        lock (_gate) { samples = _samples.ToArray(); wifi = _wifi.ToArray(); phases = _phases.ToArray(); }
        if (samples.Length < 2)
        {
            TextRenderer.DrawText(g, Loc.T("Hier erscheint der Verlauf der Übertragungsrate während eines Tests.", "The transfer rate history appears here during a test."),
                Font, area, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int axisW = (int)(64 * k), legendH = (int)(22 * k);
        bool hasWifi = wifi.Length > 0;
        int wifiH = hasWifi ? (int)(area.Height * 0.3) : 0;
        var rate = new Rectangle(area.Left + axisW, area.Top + legendH, area.Width - axisW - (int)(56 * k), area.Height - legendH - wifiH - (int)(22 * k) - (hasWifi ? (int)(10 * k) : 0));
        if (rate.Width < 40 || rate.Height < 30) return;
        double tMax = Math.Max(samples[^1].T, wifi.Length > 0 ? wifi[^1].T : 0);
        tMax = Math.Max(tMax, 1);
        float X(double t) => rate.Left + (float)(t / tMax * rate.Width);

        // Legend: phases
        int lx = area.Left + axisW;
        for (int i = 0; i < phases.Length; i++)
        {
            using var b = new SolidBrush(PhaseColors[i % PhaseColors.Length]);
            g.FillRectangle(b, lx, area.Top + (int)(7 * k), (int)(14 * k), (int)(4 * k));
            Size sz = TextRenderer.MeasureText(phases[i], Font);
            TextRenderer.DrawText(g, phases[i], Font, new Point(lx + (int)(18 * k), area.Top), Theme.Text);
            lx += sz.Width + (int)(34 * k);
        }
        if (hasWifi)
        {
            foreach ((string name, Color c) in new[] { ("RSSI (dBm)", RssiColor), (Loc.T("Paketwiederholungen (%)", "Packet retries (%)"), RetryColor) })
            {
                using var b = new SolidBrush(c);
                g.FillRectangle(b, lx, area.Top + (int)(7 * k), (int)(14 * k), (int)(4 * k));
                TextRenderer.DrawText(g, name, Font, new Point(lx + (int)(18 * k), area.Top), Theme.Muted);
                lx += TextRenderer.MeasureText(name, Font).Width + (int)(34 * k);
            }
        }

        // Rate plot
        using (var back = new SolidBrush(Theme.Console)) g.FillRectangle(back, rate);
        double max = Math.Max(10, samples.Max(s => s.Mbps)) * 1.1;
        double step = NiceStep(max / 4);
        using (var gridPen = new Pen(Theme.Grid))
        {
            for (double v = 0; v <= max; v += step)
            {
                float y = rate.Bottom - (float)(v / max * rate.Height);
                g.DrawLine(gridPen, rate.Left, y, rate.Right, y);
                TextRenderer.DrawText(g, v.ToString("0", CultureInfo.InvariantCulture), Font, new Rectangle(area.Left, (int)y - (int)(9 * k), axisW - (int)(6 * k), (int)(18 * k)), Theme.DisabledText, TextFormatFlags.Right);
            }
        }
        TextRenderer.DrawText(g, "Mbit/s", Font, new Point(area.Left, rate.Top - (int)(2 * k)), Theme.DisabledText);

        // Dips / stalls as coloured bands (per phase median)
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
                using var band = new SolidBrush(Color.FromArgb(stall ? 90 : 45, stall ? Theme.Error : Theme.Warning));
                float x1 = X(steady[i - 1].T), x2 = X(s.T);
                g.FillRectangle(band, x1, rate.Top, Math.Max(1, x2 - x1), rate.Height);
            }
        }

        // Rate lines
        foreach (var phase in samples.GroupBy(s => s.Phase))
        {
            using var pen = new Pen(PhaseColors[phase.Key % PhaseColors.Length], 1.6f * k);
            PointF? prev = null;
            foreach (var s in phase.OrderBy(s => s.T))
            {
                var p = new PointF(X(s.T), rate.Bottom - (float)(Math.Min(s.Mbps, max) / max * rate.Height));
                if (prev is { } pp) g.DrawLine(pen, pp, p);
                prev = p;
            }
        }

        // Time axis
        double tStep = NiceStep(tMax / 8);
        for (double t = 0; t <= tMax + 1e-6; t += tStep)
        {
            float x = X(t);
            string label = t >= 120 ? $"{(int)t / 60}:{(int)t % 60:00}" : $"{t:0} s";
            TextRenderer.DrawText(g, label, Font, new Point((int)x - (int)(14 * k), (hasWifi ? rate.Bottom : area.Bottom - (int)(20 * k)) + (int)(2 * k)), Theme.DisabledText);
        }

        // Wi-Fi panel: RSSI (left scale -90…-20 dBm) and retry rate (right scale 0…max %)
        if (hasWifi)
        {
            var wr = new Rectangle(rate.Left, rate.Bottom + (int)(22 * k), rate.Width, wifiH - (int)(4 * k));
            if (wr.Height < 20) return;
            using (var back = new SolidBrush(Theme.Console)) g.FillRectangle(back, wr);
            using (var gridPen = new Pen(Theme.Grid)) g.DrawLine(gridPen, wr.Left, wr.Top + wr.Height / 2, wr.Right, wr.Top + wr.Height / 2);
            const double rMin = -90, rMax = -20;
            double retryMax = Math.Max(10, wifi.Max(w => w.RetryPercent) * 1.1);
            TextRenderer.DrawText(g, "-20", Font, new Rectangle(area.Left, wr.Top - (int)(2 * k), axisW - (int)(6 * k), (int)(18 * k)), RssiColor, TextFormatFlags.Right);
            TextRenderer.DrawText(g, "-90", Font, new Rectangle(area.Left, wr.Bottom - (int)(16 * k), axisW - (int)(6 * k), (int)(18 * k)), RssiColor, TextFormatFlags.Right);
            TextRenderer.DrawText(g, $"{retryMax:0} %", Font, new Point(wr.Right + (int)(4 * k), wr.Top - (int)(2 * k)), RetryColor);
            TextRenderer.DrawText(g, "0 %", Font, new Point(wr.Right + (int)(4 * k), wr.Bottom - (int)(16 * k)), RetryColor);
            void Line(Func<WifiSample, double> value, double lo, double hi, Color color)
            {
                using var pen = new Pen(color, 1.6f * k);
                PointF? prev = null;
                foreach (var w in wifi)
                {
                    double v = Math.Clamp(value(w), lo, hi);
                    var p = new PointF(X(w.T), wr.Bottom - (float)((v - lo) / (hi - lo) * wr.Height));
                    if (prev is { } pp) g.DrawLine(pen, pp, p);
                    prev = p;
                }
            }
            Line(w => w.Rssi, rMin, rMax, RssiColor);
            Line(w => w.RetryPercent, 0, retryMax, RetryColor);
            var last = wifi[^1];
            TextRenderer.DrawText(g, $"{last.Rssi} dBm · {last.Frequency} MHz · Tx {last.TxLink} / Rx {last.RxLink} Mbit/s",
                Font, new Point(wr.Left + (int)(6 * k), wr.Top + (int)(2 * k)), Theme.Muted);
        }
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag;
    }

    /// <summary>Saves PNG (chart + header with device, firmware, target, summary) and CSV with all samples.</summary>
    public (string Png, string Csv) Export(string folder, string baseName, IReadOnlyList<string> header)
    {
        Directory.CreateDirectory(folder);
        string png = Path.Combine(folder, baseName + ".png"), csv = Path.Combine(folder, baseName + ".csv");
        const int w = 1800, h = 900;
        float k = 1.6f;
        using (var bmp = new Bitmap(w, h))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Theme.Card);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var title = new Font("Segoe UI Semibold", 15f);
            using var body = new Font("Segoe UI", 11f);
            int y = 18;
            TextRenderer.DrawText(g, header.Count > 0 ? header[0] : "ADBora", title, new Point(24, y), Theme.Text);
            y += 34;
            foreach (string line in header.Skip(1))
            {
                TextRenderer.DrawText(g, line, body, new Point(24, y), Theme.Muted);
                y += 24;
            }
            Font old = Font;
            Font = body;
            Draw(g, new Rectangle(16, y + 10, w - 32, h - y - 30), k);
            Font = old;
            TextRenderer.DrawText(g, "ADBora " + AppInfo.Version + " · github.com/urscaviezel/ADBora", body, new Rectangle(0, h - 26, w - 20, 22), Theme.DisabledText, TextFormatFlags.Right);
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }

        RateSample[] samples;
        WifiSample[] wifi;
        string[] phases;
        lock (_gate) { samples = _samples.ToArray(); wifi = _wifi.ToArray(); phases = _phases.ToArray(); }
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
