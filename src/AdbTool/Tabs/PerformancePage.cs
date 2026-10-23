using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using AdbTool.Backup;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>
/// Tab "Performance": live values of the XR runtime (FPS, frame times, CPU/GPU load …) and of the
/// system (clocks, temperatures, battery, memory) via ADB, with graphs and CSV logging.
/// Works without an app on the headset; optionally switches the PFD-Metrics / OVR Metrics HUD.
/// </summary>
internal sealed class PerformancePage : UserControl, IPage
{
    private const int HistorySeconds = 300;

    private readonly AppState _state;
    private readonly DarkButton _start = Ui.Button("Messung starten", "Start monitoring", ButtonKind.Primary);
    private readonly DarkButton _stop = Ui.Button("Stoppen", "Stop", ButtonKind.Danger);
    private readonly CheckBox _csv = Ui.CheckBox("Als CSV protokollieren", "Log to CSV");
    private readonly DarkButton _openLogs = Ui.Button("Protokolle öffnen", "Open logs");
    private readonly DarkButton _select = Ui.Button("Werte auswählen …", "Choose values …");
    private readonly DarkButton _install = Ui.Button("", "");
    private readonly DarkButton _hudOn = Ui.Button("HUD im Headset ein", "HUD on headset on");
    private readonly DarkButton _hudOff = Ui.Button("HUD aus", "HUD off");
    private readonly Label _status = Ui.Value("");
    private readonly Label _sources = Ui.Value("");
    private readonly FlowLayoutPanel _tiles = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 4) };
    private readonly MetricChart _chart = new() { Dock = DockStyle.Bottom, Height = 300 };
    private readonly Label _empty = Ui.Label("Noch keine Werte – „Messung starten“ drücken. Die XR-Werte (FPS, Frame-Zeiten …) erscheinen, sobald eine VR-App läuft.",
        "No values yet – press \"Start monitoring\". The XR values (FPS, frame times …) appear as soon as a VR app is running.", muted: true);
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    private readonly object _lock = new();
    private readonly Dictionary<string, double> _values = new();
    private Dictionary<string, double> _lastSnapshot = new();
    private readonly Dictionary<string, Queue<double>> _history = new();
    private readonly Dictionary<string, MetricTile> _tileMap = new();
    private readonly List<string> _graphs;
    private readonly HashSet<string> _hidden;
    private const int MaxGraphs = 6;
    private Card? _chartCard;
    private readonly PerfMetrics.SystemParser _system = new();

    private Process? _logcat;
    private Process? _sampler;
    private string? _serial;
    private string _model = "";
    private long _lastAppRuntime, _lastRuntime, _lastSystem;
    private StreamWriter? _csvWriter;
    private string? _csvFile;
    private string? _hudPackage;      // installed HUD app (PFD-Metrics HUD / OVR Metrics Tool)
    private string? _installKind;     // "pfd" / "quest" when the HUD app is missing
    private string? _deviceKind;      // "pfd" / "quest" / null
    private bool _installing;

    public PerformancePage(AppState state)
    {
        _state = state;
        _graphs = new List<string>(state.Settings.MetricsGraphList ?? PerfMetrics.DefaultGraphs.ToList());
        List<string> shown = state.Settings.MetricsShown ?? PerfMetrics.DefaultShown.ToList();
        _hidden = new HashSet<string>(PerfMetrics.All.Select(m => m.Key).Where(k => !shown.Contains(k)));
        BackColor = Theme.Background;
        DoubleBuffered = true;
        Padding = new Padding(20, 16, 20, 16);
        BuildUi();

        _tick.Tick += (_, _) => OnTick();
        _state.SelectedDeviceChanged += () => { if (_serial is not null && _state.SelectedDevice?.Serial != _serial) StopMonitoring(); UpdateEnabled(); _ = DetectHudAsync(); };
        _state.AdbChanged += UpdateEnabled;
        Loc.LanguageChanged += () => { foreach (MetricTile t in _tileMap.Values) t.Invalidate(); _chart.Invalidate(); UpdateEnabled(); };
        HandleDestroyed += (_, _) => StopMonitoring();
        UpdateEnabled();
    }

    private void BuildUi()
    {
        var top = new Card("Leistung", "Performance") { Dock = DockStyle.Top };
        var box = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Dock = DockStyle.Top };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var hint = Ui.Label(
            "Liest die Leistungswerte der XR-Runtime (Play For Dream, Meta Quest) und des Systems in Echtzeit per ADB – ohne App auf dem Headset. Kachel anklicken = im Diagramm anzeigen (bis 6).",
            "Reads the performance values of the XR runtime (Play For Dream, Meta Quest) and of the system in real time via ADB – no app needed on the headset. Click a tile to show it in the chart (up to 6).", muted: true);
        hint.Margin = new Padding(0, 0, 0, 8);
        Ui.WrapTo(hint, this, 40);
        _start.Click += async (_, _) => await StartMonitoringAsync();
        _stop.Click += (_, _) => StopMonitoring();
        _csv.Checked = _state.Settings.MetricsCsv;
        _csv.CheckedChanged += (_, _) =>
        {
            _state.Settings.MetricsCsv = _csv.Checked;
            _state.Settings.Save();
            if (_serial is not null) { if (_csv.Checked) OpenCsv(); else CloseCsv(); }
        };
        _csv.Margin = new Padding(8, 8, 16, 0);
        _openLogs.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(_state.Settings.MetricsFolder); } catch { }
            SettingsPage.OpenPath(_state.Settings.MetricsFolder);
        };
        _hudOn.Click += async (_, _) => await HudAsync(true);
        _hudOff.Click += async (_, _) => await HudAsync(false);
        _select.Click += (_, _) => ChooseValues();
        _install.Click += async (_, _) => await InstallHudAsync();
        // Two button groups (monitoring / display + HUD) that wrap as a whole when the window is narrow
        var monitor = Ui.Row(_start, _stop, _csv, _openLogs);
        var display = Ui.Row(_select, _hudOn, _hudOff, _install);
        monitor.Margin = new Padding(0, 0, 16, 6);
        display.Margin = new Padding(0, 0, 0, 6);
        var row = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0)
        };
        row.Controls.Add(monitor);
        row.Controls.Add(display);
        void FitRow()
        {
            int width = ClientSize.Width - Padding.Horizontal - Theme.S(40);
            if (width > 100) row.MaximumSize = new Size(width, 0);
        }
        Resize += (_, _) => FitRow();
        FitRow();
        _status.Margin = new Padding(0, 8, 0, 0);
        _sources.Margin = new Padding(0, 2, 0, 0);
        _sources.ForeColor = Theme.Muted;
        Ui.WrapTo(_status, this, 40);
        Ui.WrapTo(_sources, this, 40);
        box.Controls.Add(hint);
        box.Controls.Add(row);
        box.Controls.Add(_status);
        box.Controls.Add(_sources);
        top.SetContent(box);

        _empty.Margin = new Padding(4, 12, 0, 0);
        _tiles.Controls.Add(_empty);
        Theme.UseDarkScrollBars(_tiles);
        var chartCard = _chartCard = new Card("Verlauf (letzte 2 Minuten)", "History (last 2 minutes)") { Dock = DockStyle.Bottom, Height = 340, Margin = new Padding(0) };
        _chart.Dock = DockStyle.Fill;
        chartCard.SetContent(_chart, fill: true);

        Controls.Add(_tiles);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent });
        Controls.Add(chartCard);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Color.Transparent });
        Controls.Add(top);
        FitChart();
    }

    /// <summary>Chart height follows the number of graphs.</summary>
    private void FitChart()
    {
        if (_chartCard is null) return;
        int rows = Math.Max(1, _graphs.Count);
        _chartCard.Visible = _graphs.Count > 0;
        _chartCard.Height = Theme.S(Math.Clamp(60 + rows * 84, 160, 600));
    }

    public void OnActivated()
    {
        UpdateEnabled();
        _ = DetectHudAsync();
    }

    public void CancelOperation() => StopMonitoring();

    private bool Running => _serial is not null;

    private void UpdateEnabled()
    {
        if (IsDisposed) return;
        bool ready = _state.HasAdb && _state.SelectedDevice is { IsReady: true };
        _start.Enabled = ready && !Running;
        _stop.Enabled = Running;
        _hudOn.Visible = _hudOff.Visible = _hudPackage is not null;
        _hudOn.Enabled = _hudOff.Enabled = ready && _hudPackage is not null;
        _install.Visible = _installKind is not null;
        _install.Enabled = ready && !_installing;
        _install.Text = _installKind == "pfd"
            ? Loc.T("PFD-Metrics auf dem Headset installieren", "Install PFD-Metrics on the headset")
            : Loc.T("OVR Metrics Tool (Meta Store) …", "OVR Metrics Tool (Meta Store) …");
        if (!Running)
            _status.Text = !_state.HasAdb
                ? Loc.T("ADB ist nicht eingerichtet (Tab „Allgemeine Einstellungen“).", "ADB is not set up (\"General settings\" tab).")
                : _state.SelectedDevice is { IsReady: true } d
                    ? Loc.T($"Bereit: {d.DisplayName} ({d.Serial})", $"Ready: {d.DisplayName} ({d.Serial})")
                    : Loc.T("Kein bereites Gerät ausgewählt.", "No ready device selected.");
    }

    // ------------------------------------------------------------------
    // Start / stop
    // ------------------------------------------------------------------

    private async Task StartMonitoringAsync()
    {
        AdbDevice? device = _state.SelectedDevice;
        if (Running || device is not { IsReady: true } || !_state.HasAdb) return;
        var adb = new AdbClient(_state.AdbPath!, device.Serial);

        // Thermal zone names once (the loop only sends the temperatures)
        try
        {
            AdbResult zones = await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(15), "shell", PerfMetrics.ZoneTypesScript);
            _system.ZoneTypes = zones.Output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
        }
        catch { _system.ZoneTypes = Array.Empty<string>(); }

        lock (_lock)
        {
            _values.Clear();
            _history.Clear();
        }
        _lastAppRuntime = _lastRuntime = _lastSystem = 0;
        _serial = device.Serial;
        _model = device.DisplayName;

        _logcat = StartAdb(adb, new[] { "logcat", "-v", "epoch", "-v", "uid", "-T", "1", "-s" }.Concat(PerfMetrics.LogcatFilter.Split(' ')).ToArray(), OnLogLine);
        _sampler = StartAdb(adb, new[] { "shell", PerfMetrics.SamplerScript }, OnSystemLine);
        if (_csv.Checked) OpenCsv();
        _tick.Start();
        _status.Text = Loc.T($"Messung läuft: {device.DisplayName} ({device.Serial})", $"Monitoring: {device.DisplayName} ({device.Serial})");
        UpdateEnabled();
    }

    private Process? StartAdb(AdbClient adb, string[] args, Action<string> onLine)
    {
        try
        {
            var p = new Process { StartInfo = adb.CreateStartInfo(args), EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) onLine(l); };
            p.ErrorDataReceived += (_, _) => { };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            return null;
        }
    }

    private void StopMonitoring()
    {
        _tick.Stop();
        AdbClient.KillQuietly(_logcat);
        AdbClient.KillQuietly(_sampler);
        _logcat?.Dispose();
        _sampler?.Dispose();
        _logcat = _sampler = null;
        bool was = _serial is not null;
        _serial = null;
        CloseCsv();
        if (was && !IsDisposed)
        {
            _status.Text = Loc.T("Messung gestoppt.", "Monitoring stopped.");
            UpdateEnabled();
        }
    }

    // ------------------------------------------------------------------
    // Data (background threads)
    // ------------------------------------------------------------------

    private void OnLogLine(string line)
    {
        var parsed = new Dictionary<string, double>();
        if (!PerfMetrics.ParseLogLine(line, parsed, out bool systemProcess)) return;
        long now = Environment.TickCount64;
        // Lines of the running app win over those of system processes (home, shell)
        if (systemProcess && now - _lastAppRuntime < 2500) return;
        if (!systemProcess && line.Contains("PerfStats.cpp")) _lastAppRuntime = now;
        _lastRuntime = now;
        lock (_lock)
            foreach (var (k, v) in parsed) _values[k] = v;
    }

    private void OnSystemLine(string line)
    {
        var parsed = new Dictionary<string, double>();
        lock (_lock)
        {
            _system.Parse(line, parsed);
            foreach (var (k, v) in parsed) _values[k] = v;
        }
        if (line.StartsWith("@E", StringComparison.Ordinal)) _lastSystem = Environment.TickCount64;
    }

    // ------------------------------------------------------------------
    // UI tick (1 Hz): history, tiles, chart, CSV
    // ------------------------------------------------------------------

    private void OnTick()
    {
        if (!Running) return;
        long now = Environment.TickCount64;
        bool runtimeLive = now - _lastRuntime < 3500;
        bool systemLive = now - _lastSystem < 4000;
        Dictionary<string, double> snapshot;
        lock (_lock)
        {
            snapshot = new Dictionary<string, double>(_values);
            _lastSnapshot = snapshot;
            if (!runtimeLive)
            {
                // No XR app running: drop the runtime / compositor values instead of freezing them
                foreach (PerfMetric m in PerfMetrics.All.Where(m => m.Group != MetricGroup.System))
                {
                    snapshot.Remove(m.Key);
                    _values.Remove(m.Key);
                }
            }
        }

        foreach (PerfMetric m in PerfMetrics.All)
        {
            if (!_history.TryGetValue(m.Key, out Queue<double>? q))
                _history[m.Key] = q = new Queue<double>();
            q.Enqueue(snapshot.TryGetValue(m.Key, out double v) ? v : double.NaN);
            while (q.Count > HistorySeconds) q.Dequeue();
        }

        UpdateTiles(snapshot);
        _chart.SetData(_graphs.Select(k => (PerfMetrics.Find(k)!, _history.TryGetValue(k, out var h) ? h.ToArray() : Array.Empty<double>())).ToList());

        string app = _system.ForegroundApp;
        // Battery/power readings are distorted while the headset is charging over USB (only meaningful standalone / Wi-Fi ADB)
        bool powerShown = new[] { "power_voltage", "battery_current_now_amps", "power_wattage" }
            .Any(k => !_hidden.Contains(k) && snapshot.TryGetValue(k, out double pv) && !double.IsNaN(pv));
        bool usb = _serial is not null && !_serial.Contains(':') && !_serial.Contains("._adb-tls-connect");
        string usbNote = powerShown && usb
            ? Loc.T(" · Hinweis: V/A/W über USB verfälscht – nur im Standalone-Betrieb (WLAN-ADB) korrekt",
                    " · Note: V/A/W distorted over USB – only correct standalone (Wi-Fi ADB)")
            : "";
        _sources.Text = Loc.T(
            $"XR-Runtime: {(runtimeLive ? "aktiv" : "keine Daten (läuft eine VR-App?)")} · System: {(systemLive ? "aktiv" : "wartet …")}{(app.Length > 0 ? " · App: " + app : "")}{(_csvFile is not null ? " · CSV: " + Path.GetFileName(_csvFile) : "")}{usbNote}",
            $"XR runtime: {(runtimeLive ? "active" : "no data (is a VR app running?)")} · System: {(systemLive ? "active" : "waiting …")}{(app.Length > 0 ? " · App: " + app : "")}{(_csvFile is not null ? " · CSV: " + Path.GetFileName(_csvFile) : "")}{usbNote}");

        WriteCsv(snapshot, app);

        if (_logcat is { HasExited: true } && _sampler is { HasExited: true })
        {
            StopMonitoring();
            _status.Text = Loc.T("Verbindung zum Gerät verloren – Messung beendet.", "Connection to the device lost – monitoring ended.");
        }
    }

    private void UpdateTiles(Dictionary<string, double> snapshot)
    {
        bool added = false;
        foreach (PerfMetric m in PerfMetrics.All)
        {
            if (_hidden.Contains(m.Key)) continue;
            bool has = snapshot.TryGetValue(m.Key, out double v);
            if (!_tileMap.TryGetValue(m.Key, out MetricTile? tile))
            {
                if (!has) continue;
                tile = new MetricTile(m) { Selected = _graphs.Contains(m.Key) };
                tile.Click += (_, _) => ToggleGraph(m.Key);
                _tileMap[m.Key] = tile;
                added = true;
            }
            tile.SetValue(has ? v : double.NaN);
        }
        if (added || (_tiles.Controls.Contains(_empty) && _tileMap.Keys.Any(k => !_hidden.Contains(k))))
        {
            Ui.Frozen(_tiles, () =>
            {
                _tiles.Controls.Clear();
                foreach (PerfMetric m in PerfMetrics.All)
                    if (!_hidden.Contains(m.Key) && _tileMap.TryGetValue(m.Key, out MetricTile? t)) _tiles.Controls.Add(t);
            });
        }
    }

    private void ToggleGraph(string key)
    {
        if (!_graphs.Remove(key))
        {
            if (_graphs.Count >= MaxGraphs) _graphs.RemoveAt(0);
            _graphs.Add(key);
        }
        GraphsChanged();
    }

    private void GraphsChanged()
    {
        _state.Settings.MetricsGraphList = _graphs.ToList();
        _state.Settings.MetricsShown = PerfMetrics.All.Select(m => m.Key).Where(k => !_hidden.Contains(k)).ToList();
        _state.Settings.Save();
        foreach (var (k, t) in _tileMap) t.Selected = _graphs.Contains(k);
        FitChart();
        _chart.SetData(_graphs.Select(k => (PerfMetrics.Find(k)!, _history.TryGetValue(k, out var h) ? h.ToArray() : Array.Empty<double>())).ToList());
    }

    private void ChooseValues()
    {
        if (!MetricSelectDialog.Show(FindForm(), _hidden, _graphs, MaxGraphs, _serial is null ? new Dictionary<string, double>() : _lastSnapshot, _deviceKind, _hudPackage == PfdHud)) return;
        // Rebuild the tiles with the new selection
        Ui.Frozen(_tiles, () =>
        {
            _tiles.Controls.Clear();
            foreach (PerfMetric m in PerfMetrics.All)
                if (!_hidden.Contains(m.Key) && _tileMap.TryGetValue(m.Key, out MetricTile? t)) _tiles.Controls.Add(t);
            if (_tiles.Controls.Count == 0) _tiles.Controls.Add(_empty);
        });
        GraphsChanged();
    }

    // ------------------------------------------------------------------
    // CSV
    // ------------------------------------------------------------------

    private void OpenCsv()
    {
        if (_csvWriter is not null || _serial is null) return;
        try
        {
            Directory.CreateDirectory(_state.Settings.MetricsFolder);
            _csvFile = Path.Combine(_state.Settings.MetricsFolder, $"ADBora_Metrics_{BackupService.SafeName(_model, 40)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
            _csvWriter = new StreamWriter(_csvFile, false, new UTF8Encoding(false)) { AutoFlush = true };
            _csvWriter.WriteLine("time,timestamp_ms,app," + string.Join(",", PerfMetrics.All.Select(m => m.Key)));
        }
        catch (Exception ex)
        {
            _csvWriter = null;
            _csvFile = null;
            _status.Text = Loc.T("CSV konnte nicht angelegt werden: ", "Could not create CSV: ") + ex.Message;
        }
    }

    private void CloseCsv()
    {
        try { _csvWriter?.Dispose(); } catch { }
        _csvWriter = null;
        if (_csvFile is not null && !IsDisposed)
            _sources.Text = Loc.T($"CSV gespeichert: {_csvFile}", $"CSV saved: {_csvFile}");
        _csvFile = null;
    }

    private void WriteCsv(Dictionary<string, double> snapshot, string app)
    {
        if (_csvWriter is null) return;
        var sb = new StringBuilder();
        DateTime now = DateTime.Now;
        sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
          .Append(DateTimeOffset.Now.ToUnixTimeMilliseconds()).Append(',').Append(app);
        foreach (PerfMetric m in PerfMetrics.All)
        {
            sb.Append(',');
            if (snapshot.TryGetValue(m.Key, out double v) && !double.IsNaN(v))
                sb.Append(v.ToString("0.###", CultureInfo.InvariantCulture));
        }
        try { _csvWriter.WriteLine(sb.ToString()); } catch { }
    }

    // ------------------------------------------------------------------
    // Optional: HUD app on the headset (PFD-Metrics / OVR Metrics Tool)
    // ------------------------------------------------------------------

    // PFD-Metrics: HUD package with the receiver; Meta: OVR Metrics Tool
    private const string PfdHud = "com.pfd.metricstool.hud";
    private const string OvrMetrics = "com.oculus.ovrmonitormetricsservice";
    private const string PfdRepoApi = "https://api.github.com/repos/urscaviezel/PFD-Metrics/releases/latest";
    private const string OvrStoreUrl = "https://www.meta.com/de-de/experiences/ovr-metrics-tool/2372625889463779/";

    private async Task DetectHudAsync()
    {
        AdbDevice? device = _state.SelectedDevice;
        string? found = null, install = null, kind = null;
        if (device is { IsReady: true } && _state.HasAdb)
        {
            try
            {
                var adb = new AdbClient(_state.AdbPath!, device.Serial);
                AdbResult r = await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10),
                    "shell", "getprop ro.product.manufacturer; pm list packages | grep -E 'metricstool|ovrmonitormetricsservice'; dumpsys package " + OvrMetrics + " | grep -m1 versionName");
                string o = r.Output;
                found = o.Contains("package:" + PfdHud + "\n") || o.TrimEnd().EndsWith("package:" + PfdHud) || o.Contains("package:" + PfdHud + "\r") ? PfdHud
                      : o.Contains("package:" + OvrMetrics) && !Regex.IsMatch(o, @"versionName=\S*stub", RegexOptions.IgnoreCase) ? OvrMetrics : null; // Quest: "1.0-stub" = placeholder until installed
                bool pfd = o.Contains("Play For Dream", StringComparison.OrdinalIgnoreCase) || device.DisplayName.Contains("PFDM", StringComparison.OrdinalIgnoreCase);
                bool quest = o.Contains("Oculus", StringComparison.OrdinalIgnoreCase) || device.DisplayName.Contains("Quest", StringComparison.OrdinalIgnoreCase);
                if (found is null) install = pfd ? "pfd" : quest ? "quest" : null;
                kind = pfd ? "pfd" : quest ? "quest" : null;
            }
            catch { }
        }
        if (IsDisposed) return;
        _hudPackage = found;
        _installKind = install;
        _deviceKind = kind;
        UpdateEnabled();
    }

    private async Task HudAsync(bool on)
    {
        AdbDevice? device = _state.SelectedDevice;
        if (_hudPackage is null || device is not { IsReady: true }) return;
        string command = _hudPackage == PfdHud
            ? $"am broadcast -n {PfdHud}/com.pfd.metricstool.SettingsBroadcastReceiver -a com.pfd.metricstool.{(on ? "ENABLE_OVERLAY" : "DISABLE_OVERLAY")}"
            : $"am broadcast -n {OvrMetrics}/.SettingsBroadcastReceiver -a {OvrMetrics}.{(on ? "ENABLE_OVERLAY" : "DISABLE_OVERLAY")}";
        AdbResult r = await new AdbClient(_state.AdbPath!, device.Serial).CaptureAsync(CancellationToken.None, "shell", command);
        _status.Text = r.Ok && !r.Output.Contains("Error", StringComparison.OrdinalIgnoreCase)
            ? Loc.T($"HUD {(on ? "eingeschaltet" : "ausgeschaltet")} ({_hudPackage}).", $"HUD switched {(on ? "on" : "off")} ({_hudPackage}).")
            : Loc.T("HUD-Befehl fehlgeschlagen: ", "HUD command failed: ") + r.Combined.Trim();
    }

    /// <summary>
    /// PFD-Metrics: download the latest Windows release from GitHub and run its own setup
    /// (installs both APKs and grants the permissions). Quest: open the Meta Store page.
    /// </summary>
    private async Task InstallHudAsync()
    {
        AdbDevice? device = _state.SelectedDevice;
        if (_installKind is null || device is not { IsReady: true } || _installing) return;
        if (_installKind == "quest")
        {
            UpdateUi.OpenUrl(OvrStoreUrl);
            _status.Text = Loc.T("Meta Store geöffnet – OVR Metrics Tool dort für dein Konto „holen“; danach im Headset installieren.",
                                 "Meta Store opened – \"get\" the OVR Metrics Tool for your account there, then install it on the headset.");
            return;
        }

        _installing = true;
        UpdateEnabled();
        try
        {
            _status.Text = Loc.T("Suche die neueste PFD-Metrics-Version auf GitHub …", "Looking up the latest PFD-Metrics release on GitHub …");
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"ADBora/{AppInfo.Version}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var root = System.Text.Json.Nodes.JsonNode.Parse(await client.GetStringAsync(PfdRepoApi))!;
            string tag = root["tag_name"]?.GetValue<string>() ?? "?";
            var asset = (root["assets"] as System.Text.Json.Nodes.JsonArray)?
                .FirstOrDefault(a => (a?["name"]?.GetValue<string>() ?? "").EndsWith("-Windows.zip", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(Loc.T($"Im Release {tag} wurde kein Windows-ZIP gefunden.", $"No Windows ZIP found in release {tag}."));
            string name = asset["name"]!.GetValue<string>();
            string digest = asset["digest"]?.GetValue<string>() ?? "";

            string dir = Path.Combine(Path.GetTempPath(), "ADBora-PFDMetrics-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, name);
            _status.Text = Loc.T($"Lade {name} …", $"Downloading {name} …");
            await using (var src = await client.GetStreamAsync(asset["browser_download_url"]!.GetValue<string>()))
            await using (var dst = File.Create(zip))
                await src.CopyToAsync(dst);
            if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = File.OpenRead(zip);
                string actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream)).ToLowerInvariant();
                if (actual != digest[7..].ToLowerInvariant())
                    throw new InvalidDataException(Loc.T("Prüfsumme des Downloads stimmt nicht.", "Download checksum mismatch."));
            }
            string extract = Path.Combine(dir, "release");
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, extract);
            string setup = Directory.EnumerateFiles(extract, "setup.cmd", SearchOption.AllDirectories).FirstOrDefault()
                           ?? throw new InvalidDataException(Loc.T("setup.cmd fehlt im Release.", "setup.cmd is missing in the release."));

            // The official setup installs both APKs and grants all permissions (visible console window)
            _status.Text = Loc.T($"PFD-Metrics {tag}: Setup läuft – bitte das Konsolenfenster beachten und am Ende eine Taste drücken.",
                                 $"PFD-Metrics {tag}: setup is running – watch the console window and press a key at the end.");
            var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{setup}\" -AdbPath \"{_state.AdbPath}\" -NoDownload -Serial {device.Serial}\"")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(setup)!
            };
            using Process p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            _status.Text = p.ExitCode == 0
                ? Loc.T($"PFD-Metrics {tag} installiert. Im Headset Monitoring einschalten und den Log-Zugriff erlauben.",
                        $"PFD-Metrics {tag} installed. Enable monitoring in the headset and allow log access.")
                : Loc.T($"PFD-Metrics-Setup beendet mit Code {p.ExitCode} – Meldungen im Konsolenfenster beachten.",
                        $"PFD-Metrics setup ended with code {p.ExitCode} – see the messages in the console window.");
            try { Directory.Delete(dir, true); } catch { }
        }
        catch (Exception ex)
        {
            _status.Text = Loc.T("Installation fehlgeschlagen: ", "Installation failed: ") + ex.Message;
        }
        finally
        {
            _installing = false;
            await DetectHudAsync();
        }
    }
}

/// <summary>Which values are shown as tiles and which get a graph.</summary>
internal static class MetricSelectDialog
{
    private const int ColName = 0, ColValue = 1, ColShow = 2, ColGraph = 3;

    public static bool Show(IWin32Window? owner, HashSet<string> hidden, List<string> graphs, int maxGraphs, IReadOnlyDictionary<string, double> current,
                            string? deviceKind = null, bool pfdMetricsInstalled = false)
    {
        bool measuring = current.Count > 0;
        bool Has(string k) => current.TryGetValue(k, out double v) && !double.IsNaN(v);
        var show = PerfMetrics.All.ToDictionary(m => m.Key, m => !hidden.Contains(m.Key));
        var graph = PerfMetrics.All.ToDictionary(m => m.Key, m => graphs.Contains(m.Key));
        var order = new List<string>(graphs);

        using Form dialog = Ui.Dialog(Loc.T("Werte auswählen", "Choose values"), 780, 700);
        var info = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = Theme.S(78), ForeColor = Theme.Muted,
            Text = Loc.T($"„Anzeigen“ = Kachel im Tab, „Graph“ = Verlauf im Diagramm (höchstens {maxGraphs}). Nicht jedes Headset liefert jeden Wert – "
                         + (measuring ? "„Aktuell“ zeigt, was das Gerät gerade liefert (– = nicht verfügbar)." : "starte eine Messung, dann zeigt „Aktuell“, was das Gerät liefert.")
                         + " Akku-Strom und Leistungsaufnahme (W) beim Play For Dream kommen von PFD-Metrics – dort muss die Messung laufen. Spannung, Strom und Leistung stimmen nur im Standalone-Betrieb (ADB über WLAN); über USB verfälscht das Laden die Werte.",
                         $"\"Show\" = tile in the tab, \"Graph\" = history in the chart (at most {maxGraphs}). Not every headset delivers every value – "
                         + (measuring ? "\"Current\" shows what the device delivers right now (– = not available)." : "start monitoring and \"Current\" shows what the device delivers.")
                         + " Battery current and power draw (W) on the Play For Dream come from PFD-Metrics – its monitoring must be running. Voltage, current and power are only correct in standalone use (ADB over Wi-Fi); over USB, charging distorts them.")
        };
        var grid = new DataGridView();
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardBorder, Padding = new Padding(1) };
        grid.Dock = DockStyle.Fill;
        host.Controls.Add(grid);
        grid.BackgroundColor = Theme.Card;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = Theme.Grid;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.TableHeader, ForeColor = Theme.Text, Font = Theme.BoldFont, SelectionBackColor = Theme.TableHeader, SelectionForeColor = Theme.Text };
        grid.ColumnHeadersHeight = Theme.S(34);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.BaseFont, SelectionBackColor = Theme.Selection, SelectionForeColor = Theme.Text };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.TableAlt };
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.ReadOnly = true;
        grid.RowTemplate.Height = Theme.S(30);
        Theme.UseDarkScrollBars(grid);
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(grid, true);
        grid.Columns.Add(new DataGridViewTextBoxColumn { AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, HeaderText = Loc.T("Wert", "Value") });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(170), HeaderText = Loc.T("Aktuell", "Current") });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(90), HeaderText = Loc.T("Anzeigen", "Show") });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(90), HeaderText = "Graph" });
        foreach (DataGridViewColumn c in grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
        grid.Columns[ColShow].HeaderCell.Style.Alignment = grid.Columns[ColGraph].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;

        string GroupText(MetricGroup g) => g switch
        {
            MetricGroup.Runtime => "XR-Runtime",
            MetricGroup.Compositor => "Compositor",
            _ => "System"
        };
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
            int r = grid.Rows.Add($"{GroupText(m.Group)} · {m.Label}{(m.Unit.Length > 0 ? $" ({m.Unit})" : "")}", now, "", "");
            grid.Rows[r].Tag = m.Key;
            if (measuring && !Has(m.Key)) grid.Rows[r].DefaultCellStyle.ForeColor = Theme.Muted;
        }

        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < ColShow || e.Graphics is null || grid.Rows[e.RowIndex].Tag is not string key) return;
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            bool on = e.ColumnIndex == ColShow ? show[key] : graph[key];
            int bs = Theme.S(18);
            var box = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - bs) / 2, e.CellBounds.Y + (e.CellBounds.Height - bs) / 2, bs, bs);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.RoundedRect(box, Theme.S(4)))
            using (var fill = new SolidBrush(on ? Theme.Primary : Theme.Input))
            using (var pen = new Pen(on ? Theme.PrimaryBorder : Theme.ButtonBorder, 1.4f))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(pen, path);
            }
            if (on)
            {
                using var tick = new Pen(Color.White, Theme.S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                float k = bs / 20f;
                e.Graphics.DrawLines(tick, new[] { new PointF(box.X + 5 * k, box.Y + 10.5f * k), new PointF(box.X + 8.5f * k, box.Y + 14 * k), new PointF(box.X + 15 * k, box.Y + 6.5f * k) });
            }
            e.Handled = true;
        };
        grid.MouseDown += (_, e) =>
        {
            DataGridView.HitTestInfo hit = grid.HitTest(e.X, e.Y);
            if (hit.Type != DataGridViewHitTestType.Cell || grid.Rows[hit.RowIndex].Tag is not string key) return;
            if (hit.ColumnIndex == ColShow) show[key] = !show[key];
            else if (hit.ColumnIndex == ColGraph)
            {
                if (graph[key]) { graph[key] = false; order.Remove(key); }
                else
                {
                    if (order.Count >= maxGraphs) { graph[order[0]] = false; order.RemoveAt(0); }
                    graph[key] = true;
                    order.Add(key);
                }
            }
            else return;
            grid.Invalidate();
        };

        var ok = Ui.Button("Übernehmen", "Apply", ButtonKind.Primary);
        var cancel = Ui.Button("Abbrechen", "Cancel");
        var all = measuring ? Ui.Button("Alle verfügbaren", "All available") : Ui.Button("Alle anzeigen", "Show all");
        var defaults = Ui.Button("Standard", "Defaults");
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        all.Click += (_, _) => { foreach (string k in show.Keys.ToList()) show[k] = measuring ? Has(k) : !(deviceKind == "pfd" && PerfMetrics.QuestOnly.Contains(k)); grid.Invalidate(); };
        defaults.Click += (_, _) =>
        {
            foreach (string k in show.Keys.ToList()) show[k] = PerfMetrics.DefaultShown.Contains(k);
            order = PerfMetrics.DefaultGraphs.ToList();
            foreach (string k in graph.Keys.ToList()) graph[k] = order.Contains(k);
            grid.Invalidate();
        };
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Color.Transparent };
        cancel.Margin = new Padding(0);
        right.Controls.Add(cancel);
        right.Controls.Add(ok);
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Color.Transparent };
        left.Controls.Add(all);
        left.Controls.Add(defaults);
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(46), Padding = new Padding(0, Theme.S(10), 0, 0), BackColor = Color.Transparent };
        buttons.Controls.Add(left);
        buttons.Controls.Add(right);
        dialog.Controls.Add(host);
        dialog.Controls.Add(info);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (Ui.ShowDialog(dialog, owner) != DialogResult.OK) return false;
        hidden.Clear();
        foreach (var (k, v) in show) if (!v) hidden.Add(k);
        graphs.Clear();
        graphs.AddRange(order.Where(k => graph[k]));
        return true;
    }
}

/// <summary>Small value tile (label, value, unit); click = toggle graph.</summary>
internal sealed class MetricTile : Control
{
    private readonly PerfMetric _metric;
    private string _text = "–";
    private bool _selected;
    private bool _hover;
    private static readonly Font ValueFont = new("Segoe UI Semibold", 15f);

    public MetricTile(PerfMetric metric)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _metric = metric;
        Size = new Size(Theme.S(176), Theme.S(66));
        Margin = new Padding(0, 0, Theme.S(8), Theme.S(8));
        Cursor = Cursors.Hand;
        Font = Theme.BaseFont;
    }

    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Invalidate(); } }
    }

    public void SetValue(double v)
    {
        string text = _metric.Format(v);
        if (text == _text) return;
        _text = text;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Theme.RoundedRect(rect, Theme.S(8)))
        using (var fill = new SolidBrush(_hover ? Theme.ButtonHover : Theme.Card))
        using (var pen = new Pen(_selected ? Theme.Accent : Theme.CardBorder, _selected ? 2f : 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        var labelRect = new Rectangle(Theme.S(10), Theme.S(6), Width - Theme.S(20), Theme.S(20));
        TextRenderer.DrawText(g, _metric.Label, Theme.BaseFont, labelRect, Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        string text = _text + (_metric.Unit.Length > 0 && _text != "–" ? " " + _metric.Unit : "");
        var valueRect = new Rectangle(Theme.S(10), Theme.S(26), Width - Theme.S(20), Theme.S(34));
        TextRenderer.DrawText(g, text, ValueFont, valueRect, _text == "–" ? Theme.DisabledText : Theme.Text, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Up to 4 stacked line charts (one per metric) with current/min/avg/max.</summary>
internal sealed class MetricChart : Control
{
    private static readonly Color[] Colors =
    {
        ColorTranslator.FromHtml("#67d5b5"), ColorTranslator.FromHtml("#6fa8ff"),
        ColorTranslator.FromHtml("#e0b060"), ColorTranslator.FromHtml("#e07bb5"),
    };
    private const int Window = 120;
    private List<(PerfMetric Metric, double[] Values)> _data = new();

    public MetricChart()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Font = Theme.BaseFont;
    }

    public void SetData(List<(PerfMetric, double[])> data)
    {
        _data = data;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Card);
        if (_data.Count == 0)
        {
            TextRenderer.DrawText(g, Loc.T("Kachel anklicken, um sie hier als Verlauf anzuzeigen.", "Click a tile to show its history here."),
                Theme.BaseFont, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int n = _data.Count;
        int gap = Theme.S(8);
        int rowH = (Height - gap * (n - 1)) / n;
        int labelW = Theme.S(200);
        for (int i = 0; i < n; i++)
        {
            var (metric, all) = _data[i];
            Color color = Colors[i % Colors.Length];
            int top = i * (rowH + gap);
            double[] values = all.Length > Window ? all[^Window..] : all;
            double[] valid = values.Where(v => !double.IsNaN(v)).ToArray();

            // Legend
            string cur = valid.Length > 0 && !double.IsNaN(values[^1]) ? metric.Format(values[^1]) : "–";
            string unit = metric.Unit.Length > 0 ? " " + metric.Unit : "";
            TextRenderer.DrawText(g, metric.Label, Theme.BoldFont, new Rectangle(0, top, labelW, Theme.S(20)), color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, cur + unit, Theme.SectionFont, new Rectangle(0, top + Theme.S(20), labelW, Theme.S(24)), Theme.Text, TextFormatFlags.NoPrefix);
            if (valid.Length > 0)
                TextRenderer.DrawText(g, Loc.T($"Min {metric.Format(valid.Min())} · Ø {metric.Format(valid.Average())} · Max {metric.Format(valid.Max())}",
                                               $"Min {metric.Format(valid.Min())} · avg {metric.Format(valid.Average())} · Max {metric.Format(valid.Max())}"),
                    Theme.BaseFont, new Rectangle(0, top + Theme.S(46), labelW, Theme.S(20)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // Plot area
            var plot = new Rectangle(labelW + Theme.S(8), top + Theme.S(2), Width - labelW - Theme.S(12), rowH - Theme.S(4));
            if (plot.Width < 20 || plot.Height < 10) continue;
            using (var back = new SolidBrush(Theme.Console)) g.FillRectangle(back, plot);
            double max = Math.Max(metric.GraphMax, valid.Length > 0 ? valid.Max() * 1.1 : 0);
            double min = Math.Min(0, valid.Length > 0 ? valid.Min() : 0);
            using (var gridPen = new Pen(Theme.Grid))
            {
                for (int k = 1; k < 4; k++)
                {
                    int y = plot.Top + plot.Height * k / 4;
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                }
            }
            TextRenderer.DrawText(g, metric.Format(max), Theme.BaseFont, new Point(plot.Right - Theme.S(60), plot.Top), Theme.DisabledText);
            if (values.Length < 2) continue;
            using var pen = new Pen(color, Theme.S(1.8f));
            PointF? prev = null;
            for (int k = 0; k < values.Length; k++)
            {
                double v = values[k];
                if (double.IsNaN(v)) { prev = null; continue; }
                float x = plot.Right - (float)(values.Length - 1 - k) / (Window - 1) * plot.Width;
                float y = plot.Bottom - (float)((v - min) / Math.Max(1e-9, max - min)) * plot.Height;
                var p = new PointF(x, Math.Clamp(y, plot.Top, plot.Bottom));
                if (prev is { } pp) g.DrawLine(pen, pp, p);
                prev = p;
            }
        }
    }
}
