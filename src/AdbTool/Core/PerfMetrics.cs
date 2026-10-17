using System.Globalization;
using System.Text.RegularExpressions;

namespace AdbTool.Core;

internal enum MetricGroup { Runtime, Compositor, System }

/// <summary>One performance value. Keys follow the OVR Metrics Tool / PFD-Metrics naming where possible.</summary>
internal sealed record PerfMetric(string Key, string De, string En, string Unit, int Decimals, double GraphMax, MetricGroup Group)
{
    public string Label => Loc.T(De, En);

    public string Format(double v) => double.IsNaN(v) ? "–" : v.ToString(Decimals switch { 0 => "0", 1 => "0.0", _ => "0.00" }, CultureInfo.CurrentCulture);
}

/// <summary>
/// Metric catalogue and parsers. Sources (all readable via ADB without root and without an app on the headset):
///  - logcat of the XR runtime: Play For Dream / YVR "PerfStats.cpp" + "CompositorMonitor",
///    Meta Quest "VrApi" (1 line per second),
///  - a small shell loop on the device (cpufreq, kgsl gpubusy, /proc/stat, meminfo,
///    thermal zones, dumpsys battery/thermalservice/activity).
/// </summary>
internal static class PerfMetrics
{
    public static readonly PerfMetric[] All =
    {
        // XR runtime (app)
        new("average_frame_rate", "FPS", "FPS", "", 1, 120, MetricGroup.Runtime),
        new("display_refresh_rate", "Bildrate Display", "Display refresh", "Hz", 0, 120, MetricGroup.Runtime),
        new("frame_time_milliseconds", "Frame-Zeit", "Frame time", "ms", 2, 33.3, MetricGroup.Runtime),
        new("stale_frame_count", "Stale Frames/s", "Stale frames/s", "", 0, 30, MetricGroup.Runtime),
        new("early_frame_count", "Early Frames/s", "Early frames/s", "", 0, 120, MetricGroup.Runtime),
        new("app_cpu_time_milliseconds", "App CPU-Zeit", "App CPU time", "ms", 2, 11.1, MetricGroup.Runtime),
        new("app_gpu_time_milliseconds", "App GPU-Zeit", "App GPU time", "ms", 2, 11.1, MetricGroup.Runtime),
        new("timewarp_gpu_time_milliseconds", "Timewarp GPU", "Timewarp GPU", "ms", 2, 11.1, MetricGroup.Runtime),
        new("cpu_utilization_percentage", "CPU-Last (App)", "CPU load (app)", "%", 0, 100, MetricGroup.Runtime),
        new("gpu_utilization_percentage", "GPU-Last (App)", "GPU load (app)", "%", 0, 100, MetricGroup.Runtime),
        new("cpu_level", "CPU-Level", "CPU level", "", 0, 5, MetricGroup.Runtime),
        new("gpu_level", "GPU-Level", "GPU level", "", 0, 5, MetricGroup.Runtime),
        new("foveation_level", "Foveation", "Foveation", "", 0, 4, MetricGroup.Runtime),
        new("average_prediction_milliseconds", "Vorhersage", "Prediction", "ms", 1, 60, MetricGroup.Runtime),
        new("available_memory_MB", "Freier RAM (Runtime)", "Free RAM (runtime)", "MB", 0, 16000, MetricGroup.Runtime),
        new("cpu_frequency_MHz", "CPU-Takt", "CPU clock", "MHz", 0, 3500, MetricGroup.Runtime),
        new("gpu_frequency_MHz", "GPU-Takt", "GPU clock", "MHz", 0, 1200, MetricGroup.Runtime),
        new("memory_frequency_MHz", "RAM-Takt", "Memory clock", "MHz", 0, 4000, MetricGroup.Runtime),
        new("cpu_gpu_time_milliseconds", "CPU+GPU-Zeit", "CPU+GPU time", "ms", 2, 33.3, MetricGroup.Runtime),
        new("guardian_gpu_time_milliseconds", "Guardian GPU", "Guardian GPU", "ms", 2, 2, MetricGroup.Runtime),
        new("tear_count", "Tearing", "Tearing", "", 0, 10, MetricGroup.Runtime),
        new("extra_latency_mode", "Extra-Latenz-Modus", "Extra latency mode", "", 0, 2, MetricGroup.Runtime),
        new("layer_count", "Layer", "Layers", "", 0, 16, MetricGroup.Runtime),
        // Compositor
        new("compositor_frame_rate", "Compositor FPS", "Compositor FPS", "", 1, 120, MetricGroup.Compositor),
        new("compositor_cpu_time_milliseconds", "Compositor CPU", "Compositor CPU", "ms", 2, 11.1, MetricGroup.Compositor),
        new("compositor_gpu_time_milliseconds", "Compositor GPU", "Compositor GPU", "ms", 2, 11.1, MetricGroup.Compositor),
        new("stale_frames_consecutive", "Stale in Folge", "Stale in a row", "", 0, 10, MetricGroup.Compositor),
        new("spacewarp_mode", "Spacewarp", "Spacewarp", "", 0, 3, MetricGroup.Compositor),
        // System (sampled via adb shell)
        new("system_cpu_utilization_percentage", "CPU-Last (System)", "CPU load (system)", "%", 0, 100, MetricGroup.System),
        new("system_gpu_utilization_percentage", "GPU-Last (System)", "GPU load (system)", "%", 0, 100, MetricGroup.System),
        new("system_cpu_frequency_MHz", "CPU-Takt (max.)", "CPU clock (max)", "MHz", 0, 3500, MetricGroup.System),
        new("system_gpu_frequency_MHz", "GPU-Takt (System)", "GPU clock (system)", "MHz", 0, 1200, MetricGroup.System),
        new("cpu_temperature_celcius", "CPU-Temperatur", "CPU temperature", "°C", 1, 100, MetricGroup.System),
        new("gpu_temperature_celcius", "GPU-Temperatur", "GPU temperature", "°C", 1, 100, MetricGroup.System),
        new("ddr_temperature_celcius", "RAM-Temperatur", "Memory temperature", "°C", 1, 100, MetricGroup.System),
        new("board_temperature_celcius", "Gehäuse/Board-Temp.", "Board temperature", "°C", 1, 80, MetricGroup.System),
        new("battery_level_percentage", "Akku", "Battery", "%", 0, 100, MetricGroup.System),
        new("battery_temperature_celcius", "Akku-Temperatur", "Battery temperature", "°C", 1, 60, MetricGroup.System),
        new("power_voltage", "Akku-Spannung", "Battery voltage", "V", 2, 5, MetricGroup.System),
        new("battery_current_now_amps", "Akku-Strom", "Battery current", "A", 2, 3, MetricGroup.System),
        new("power_wattage", "Leistungsaufnahme", "Power draw", "W", 2, 15, MetricGroup.System),
        new("system_available_memory_MB", "Freier RAM", "Free RAM", "MB", 0, 16000, MetricGroup.System),
        new("thermal_status", "Thermik-Status", "Thermal status", "", 0, 6, MetricGroup.System),
    };

    public static PerfMetric? Find(string key) => All.FirstOrDefault(m => m.Key == key);

    /// <summary>Default selection on a fresh install (tiles / graphs). Everything else via "Werte auswählen …".</summary>
    public static readonly string[] DefaultShown =
    {
        "average_frame_rate", "display_refresh_rate", "cpu_utilization_percentage", "gpu_utilization_percentage",
        "cpu_level", "gpu_level", "available_memory_MB", "battery_level_percentage", "battery_temperature_celcius"
    };
    public static readonly string[] DefaultGraphs = { "average_frame_rate", "cpu_utilization_percentage", "gpu_utilization_percentage" };

    /// <summary>status.json of PFD-Metrics (written every ~5 s while its monitoring runs); has current/power from BatteryManager.</summary>
    public const string PfdStatusFile = "/sdcard/Android/data/com.pfd.metricstool.hud/files/status.json";

    // ------------------------------------------------------------------
    // logcat parsing
    // ------------------------------------------------------------------

    public const string LogcatFilter = "PerfStats.cpp:I CompositorMonitor:I VrApi:I";

    private static readonly Regex Number = new(@"^[-+]?\d+(\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex Clocks = new(@"^(\d+)/(\d+)MHz$", RegexOptions.Compiled);

    private static double Num(string s)
    {
        Match m = Number.Match(s.Trim());
        return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : double.NaN;
    }

    private static (double, double) Pair(string v)
    {
        int s = v.IndexOf('/');
        return s < 0 ? (Num(v), double.NaN) : (Num(v[..s]), Num(v[(s + 1)..]));
    }

    private static double Pct(double ratio) => double.IsNaN(ratio) ? ratio : ratio <= 1.0 ? ratio * 100.0 : ratio;

    /// <summary>
    /// Parses one logcat line. Returns false if it is not a performance line.
    /// "uidSystem" = line from a system process (uid 1000); app lines take precedence.
    /// </summary>
    public static bool ParseLogLine(string line, IDictionary<string, double> values, out bool fromSystemProcess)
    {
        fromSystemProcess = false;
        int p;
        if ((p = TagEnd(line, "PerfStats.cpp")) >= 0)
        {
            string msg = line[p..].Trim();
            if (!msg.StartsWith("FPS=", StringComparison.Ordinal)) return false;
            fromSystemProcess = IsSystemUid(line[..line.IndexOf("PerfStats.cpp", StringComparison.Ordinal)]);
            ParseKeyValues(msg, values, quest: false);
            return true;
        }
        if ((p = TagEnd(line, "VrApi")) >= 0)
        {
            string msg = line[p..].Trim();
            if (!msg.StartsWith("FPS=", StringComparison.Ordinal)) return false;
            ParseKeyValues(msg, values, quest: true);
            return true;
        }
        if ((p = TagEnd(line, "CompositorMonitor")) >= 0)
        {
            string msg = line[p..].Trim();
            if (!msg.StartsWith("fps:", StringComparison.Ordinal)) return false;
            foreach (string tok in msg.Split(','))
            {
                int c = tok.IndexOf(':');
                if (c < 0) continue;
                string k = tok[..c].Trim(), v = tok[(c + 1)..];
                switch (k)
                {
                    case "fps": Put(values, "compositor_frame_rate", Num(v)); break;
                    case "cpu": Put(values, "compositor_cpu_time_milliseconds", Num(v)); break;
                    case "gpu": Put(values, "compositor_gpu_time_milliseconds", Num(v)); break;
                    case "max_stale": Put(values, "stale_frames_consecutive", Num(v)); break;
                    case "sw_mode": Put(values, "spacewarp_mode", Num(v)); break;
                }
            }
            return true;
        }
        return false;
    }

    /// <summary>Index after "&lt;tag&gt;:" (logcat pads short tags with spaces: "VrApi   :"), or -1.</summary>
    private static int TagEnd(string line, string tag)
    {
        int i = line.IndexOf(" " + tag, StringComparison.Ordinal);
        if (i < 0) return -1;
        int j = i + 1 + tag.Length;
        while (j < line.Length && line[j] == ' ') j++;
        return j < line.Length && line[j] == ':' ? j + 1 : -1;
    }

    private static bool IsSystemUid(string header)
    {
        // "<epoch> <uid> <pid> <tid> <lvl> " – uid 1000 = system process (home, shell)
        string[] t = header.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return t.Length >= 5 && (t[1] == "1000" || t[1] == "system");
    }

    private static void ParseKeyValues(string msg, IDictionary<string, double> values, bool quest)
    {
        foreach (string raw in msg.Split(','))
        {
            string tok = raw.Trim();
            Match clk = Clocks.Match(tok);
            if (clk.Success) // Quest: "2016/525MHz" follows CPU4/GPU=…
            {
                Put(values, "cpu_frequency_MHz", double.Parse(clk.Groups[1].Value, CultureInfo.InvariantCulture));
                Put(values, "gpu_frequency_MHz", double.Parse(clk.Groups[2].Value, CultureInfo.InvariantCulture));
                continue;
            }
            int eq = tok.IndexOf('=');
            if (eq < 0) continue;
            string k = tok[..eq].Trim(), v = tok[(eq + 1)..].Trim();
            if (k.StartsWith("CPU", StringComparison.Ordinal) && k.EndsWith("/GPU", StringComparison.Ordinal))
            {
                var (c, g) = Pair(v);
                Put(values, "cpu_level", c);
                Put(values, "gpu_level", g);
                continue;
            }
            switch (k)
            {
                case "FPS":
                {
                    var (fps, refresh) = Pair(v);
                    Put(values, "average_frame_rate", fps);
                    Put(values, "display_refresh_rate", refresh);
                    if (fps > 0) Put(values, "frame_time_milliseconds", 1000.0 / fps);
                    break;
                }
                case "Prd": Put(values, "average_prediction_milliseconds", Num(v)); break;
                case "Early": Put(values, "early_frame_count", Num(v)); break;
                case "Stale": Put(values, "stale_frame_count", Num(v)); break;
                case "Fov": Put(values, "foveation_level", Num(v)); break;
                case "Free": Put(values, "available_memory_MB", Num(v)); break;
                case "ATW": case "TW": Put(values, "timewarp_gpu_time_milliseconds", Num(v)); break;
                case "APP":
                {
                    var (cpu, gpu) = Pair(v); // YVR: APP=<cpu>/<gpu>
                    Put(values, "app_cpu_time_milliseconds", cpu);
                    Put(values, "app_gpu_time_milliseconds", gpu);
                    break;
                }
                case "App": Put(values, "app_gpu_time_milliseconds", Num(v)); break; // Quest: App = GPU time
                case "CPU%": Put(values, "cpu_utilization_percentage", Pct(Num(v))); break;
                case "GPU%": Put(values, "gpu_utilization_percentage", Pct(Num(v))); break;
                case "Temp" when quest: Put(values, "battery_temperature_celcius", Num(v)); break;
                case "Lat": Put(values, "extra_latency_mode", Num(v)); break;
                case "GD": Put(values, "guardian_gpu_time_milliseconds", Num(v)); break;
                case "Tear": Put(values, "tear_count", Num(v)); break;
                case "Mem": Put(values, "memory_frequency_MHz", Num(v)); break;
                case "LCnt": Put(values, "layer_count", Num(v)); break;
                case "CPU&GPU": Put(values, "cpu_gpu_time_milliseconds", Num(v)); break;
                case "Stale2/5/10/max":
                {
                    string[] parts = v.Split('/');
                    Put(values, "stale_frames_consecutive", Num(parts[^1]));
                    break;
                }
            }
        }
    }

    private static void Put(IDictionary<string, double> values, string key, double v)
    {
        if (!double.IsNaN(v)) values[key] = v;
    }

    // ------------------------------------------------------------------
    // System sampling (shell loop on the device)
    // ------------------------------------------------------------------

    /// <summary>Shell loop: one block of tagged lines per second (dumpsys only every 2nd / 3rd second).</summary>
    public const string SamplerScript =
        "i=0; while true; do " +
        "echo \"@F $(cat /sys/devices/system/cpu/cpu*/cpufreq/scaling_cur_freq 2>/dev/null | tr '\\n' ' ')\"; " +
        "echo \"@G $(cat /sys/class/kgsl/kgsl-3d0/gpubusy 2>/dev/null)\"; " +
        "echo \"@P $(cat /sys/class/kgsl/kgsl-3d0/gpu_busy_percentage 2>/dev/null)\"; " +
        "echo \"@C $(cat /sys/class/kgsl/kgsl-3d0/gpuclk 2>/dev/null)\"; " +
        "echo \"@S $(head -1 /proc/stat)\"; " +
        "echo \"@M $(grep MemAvailable /proc/meminfo)\"; " +
        "z=''; for t in /sys/class/thermal/thermal_zone*; do v=$(cat $t/temp 2>/dev/null); z=\"$z ${v:-x}\"; done; echo \"@Z$z\"; " +
        "if [ $((i % 2)) -eq 0 ]; then echo \"@B $(dumpsys battery | grep -E '^ *(level|temperature|voltage):' | tr '\\n' ' ')\"; " +
        "echo \"@H $(dumpsys thermalservice | grep -m1 'Thermal Status')\"; fi; " +
        "if [ $((i % 3)) -eq 0 ]; then echo \"@A $(dumpsys activity activities | grep -m1 topResumedActivity)\"; " +
        "echo \"@W now=$(date +%s) cur=$(cat /sys/class/power_supply/battery/current_now 2>/dev/null) $(grep -E '\\\"(time|battery_current_now_amps|power_wattage)\\\"' " + PfdStatusFile + " 2>/dev/null | tr -d ' \\\",' | tr '\\n' ' ')\"; fi; " +
        "echo @E; i=$((i+1)); sleep 1; done";

    public const string ZoneTypesScript = "for t in /sys/class/thermal/thermal_zone*; do v=$(cat $t/type 2>/dev/null); echo \"${v:-x}\"; done";

    private static readonly Regex TopActivity = new(@"u\d+\s+([\w.]+)/", RegexOptions.Compiled);

    /// <summary>State that needs the previous sample (CPU load from /proc/stat deltas).</summary>
    internal sealed class SystemParser
    {
        private long[]? _lastCpu;
        public string[] ZoneTypes { get; set; } = Array.Empty<string>();
        public string ForegroundApp { get; private set; } = "";
        public double? LastVoltage { get; private set; }
        /// <summary>True while the power values come from a running PFD-Metrics on the headset.</summary>
        public bool PowerFromPfd { get; private set; }

        public void Parse(string line, IDictionary<string, double> values)
        {
            if (line.Length < 2 || line[0] != '@') return;
            string body = line.Length > 2 ? line[2..].Trim() : "";
            switch (line[1])
            {
                case 'F':
                {
                    double max = body.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Num).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
                    Put(values, "system_cpu_frequency_MHz", max / 1000.0);
                    break;
                }
                case 'G':
                {
                    string[] f = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length == 2 && double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double busy)
                                      && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double total))
                        Put(values, "system_gpu_utilization_percentage", total > 0 ? Math.Clamp(100.0 * busy / total, 0, 100) : 0); // "0 0" = GPU idle
                    break;
                }
                case 'P':
                    Put(values, "system_gpu_utilization_percentage", Num(body));
                    break;
                case 'C':
                {
                    double hz = Num(body);
                    Put(values, "system_gpu_frequency_MHz", hz > 100000 ? hz / 1e6 : hz);
                    break;
                }
                case 'S':
                {
                    string[] f = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 5 || f[0] != "cpu") break;
                    long total = 0;
                    for (int i = 1; i < f.Length; i++) total += long.TryParse(f[i], out long x) ? x : 0;
                    long idle = (long.TryParse(f[4], out long a) ? a : 0) + (f.Length > 5 && long.TryParse(f[5], out long b) ? b : 0);
                    if (_lastCpu is { } last && total > last[0])
                        Put(values, "system_cpu_utilization_percentage", 100.0 * ((total - last[0]) - (idle - last[1])) / (total - last[0]));
                    _lastCpu = new[] { total, idle };
                    break;
                }
                case 'M':
                    Put(values, "system_available_memory_MB", Num(body.Replace("MemAvailable:", "")) / 1024.0);
                    break;
                case 'Z':
                {
                    string[] t = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    double cpu = double.NaN, gpu = double.NaN, ddr = double.NaN, board = double.NaN;
                    for (int i = 0; i < t.Length && i < ZoneTypes.Length; i++)
                    {
                        if (!double.TryParse(t[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double milli)) continue;
                        double c = milli > 1000 ? milli / 1000.0 : milli;
                        if (c <= 0 || c > 150) continue;
                        string type = ZoneTypes[i].ToLowerInvariant();
                        if (type.StartsWith("cpu")) cpu = double.IsNaN(cpu) ? c : Math.Max(cpu, c);
                        else if (type.StartsWith("gpu")) gpu = double.IsNaN(gpu) ? c : Math.Max(gpu, c);
                        else if (type == "ddr") ddr = c;
                        else if (type.StartsWith("sys-therm") || type.Contains("skin")) board = double.IsNaN(board) ? c : Math.Max(board, c);
                    }
                    Put(values, "cpu_temperature_celcius", cpu);
                    Put(values, "gpu_temperature_celcius", gpu);
                    Put(values, "ddr_temperature_celcius", ddr);
                    Put(values, "board_temperature_celcius", board);
                    break;
                }
                case 'B':
                {
                    Match l = Regex.Match(body, @"level:\s*(\d+)");
                    Match t = Regex.Match(body, @"temperature:\s*(-?\d+)");
                    Match v = Regex.Match(body, @"voltage:\s*(\d+)");
                    if (l.Success) Put(values, "battery_level_percentage", double.Parse(l.Groups[1].Value, CultureInfo.InvariantCulture));
                    if (t.Success) Put(values, "battery_temperature_celcius", double.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture) / 10.0);
                    if (v.Success)
                    {
                        double mv = double.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture);
                        LastVoltage = mv > 100 ? mv / 1000.0 : mv;
                        Put(values, "power_voltage", LastVoltage.Value);
                    }
                    break;
                }
                case 'W':
                {
                    // sysfs current (µA, only where readable) or PFD-Metrics status.json (fresh = < 20 s old)
                    Match now = Regex.Match(body, @"now=(\d+)");
                    Match cur = Regex.Match(body, @"cur=(-?\d+)");
                    Match time = Regex.Match(body, @"time:(\d+)");
                    Match amps = Regex.Match(body, @"battery_current_now_amps:(-?[\d.Ee+-]+)");
                    Match watt = Regex.Match(body, @"power_wattage:([\d.Ee+-]+)");
                    if (cur.Success && long.TryParse(cur.Groups[1].Value, out long ua) && ua != 0)
                    {
                        double a = ua / 1e6;
                        Put(values, "battery_current_now_amps", Math.Abs(a));
                        double volt = LastVoltage ?? double.NaN;
                        if (volt > 0) Put(values, "power_wattage", Math.Abs(a) * volt);
                    }
                    else if (time.Success && now.Success)
                    {
                        bool fresh = Math.Abs(long.Parse(now.Groups[1].Value) - long.Parse(time.Groups[1].Value) / 1000) < 20;
                        double a = amps.Success ? Num(amps.Groups[1].Value) : double.NaN;
                        double w = watt.Success ? Num(watt.Groups[1].Value) : double.NaN;
                        values["battery_current_now_amps"] = fresh ? Math.Abs(a) : double.NaN;
                        values["power_wattage"] = fresh ? w : double.NaN;
                        PowerFromPfd = fresh && !double.IsNaN(w);
                    }
                    break;
                }
                case 'H':
                    Put(values, "thermal_status", Num(body.Replace("Thermal Status:", "")));
                    break;
                case 'A':
                {
                    Match m = TopActivity.Match(body);
                    if (m.Success) ForegroundApp = m.Groups[1].Value;
                    break;
                }
            }
        }
    }
}
