using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AdbTool.Backup;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>
/// Tab "Performance": live values of the XR runtime (FPS, frame times, CPU/GPU load …) and of the
/// system (clocks, temperatures, battery, memory) via ADB, with graphs and CSV logging.
/// Works without an app on the headset; optionally switches the PFD-Metrics / OVR Metrics HUD.
/// </summary>
internal sealed class PerformancePage : DockPanel, IPage
{
    private const int HistorySeconds = 300;

    private readonly AppState _state;
    private readonly Button _start = Ui.Button("Messung starten", "Start monitoring", ButtonKind.Primary);
    private readonly Button _stop = Ui.Button("Stoppen", "Stop", ButtonKind.Danger);
    private readonly CheckBox _csv = Ui.CheckBox("Als CSV protokollieren", "Log to CSV");
    private readonly Button _openLogs = Ui.Button("Protokolle öffnen", "Open logs");
    private readonly Button _select = Ui.Button("Werte auswählen …", "Choose values …");
    private readonly Button _install = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _hudOn = Ui.Button("HUD im Headset ein", "HUD on headset on");
    private readonly Button _hudOff = Ui.Button("HUD aus", "HUD off");
    private readonly TextBlock _status = Ui.Value("");
    private readonly TextBlock _sources = Ui.Value("");
    private readonly WrapPanel _tiles = new() { Margin = new Thickness(0, 4) };
    private readonly MetricChart _chart = new();
    private readonly TextBlock _empty = Ui.Label("Noch keine Werte – „Messung starten“ drücken. Die XR-Werte (FPS, Frame-Zeiten …) erscheinen, sobald eine VR-App läuft.",
        "No values yet – press \"Start monitoring\". The XR values (FPS, frame times …) appear as soon as a VR app is running.", muted: true);
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

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
        Margin = new Thickness(20, 16);
        BuildUi();

        _tick.Tick += (_, _) => OnTick();
        _state.SelectedDeviceChanged += () => { if (_serial is not null && _state.SelectedDevice?.Serial != _serial) StopMonitoring(); UpdateEnabled(); _ = DetectHudAsync(); };
        _state.AdbChanged += UpdateEnabled;
        Loc.LanguageChanged += () => { foreach (MetricTile t in _tileMap.Values) t.InvalidateVisual(); _chart.InvalidateVisual(); UpdateEnabled(); };
        DetachedFromVisualTree += (_, _) => StopMonitoring();
        UpdateEnabled();
    }

    private void BuildUi()
    {
        var top = new Card("Leistung", "Performance");
        var hint = Ui.Label(
            "Liest die Leistungswerte der XR-Runtime (Play For Dream, Meta Quest) und des Systems in Echtzeit per ADB – ohne App auf dem Headset. Kachel anklicken = im Diagramm anzeigen (bis 6).",
            "Reads the performance values of the XR runtime (Play For Dream, Meta Quest) and of the system in real time via ADB – no app needed on the headset. Click a tile to show it in the chart (up to 6).", muted: true);
        hint.Margin = new Thickness(0, 0, 0, 8);
        _start.Click += async (_, _) => await StartMonitoringAsync();
        _stop.Click += (_, _) => StopMonitoring();
        _csv.IsChecked = _state.Settings.MetricsCsv;
        _csv.IsCheckedChanged += (_, _) =>
        {
            _state.Settings.MetricsCsv = _csv.IsChecked == true;
            _state.Settings.Save();
            if (_serial is not null) { if (_csv.IsChecked == true) OpenCsv(); else CloseCsv(); }
        };
        _csv.Margin = new Thickness(8, 0, 16, 0);
        _openLogs.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(_state.Settings.MetricsFolder); } catch { }
            Platform.OpenFolder(_state.Settings.MetricsFolder);
        };
        _hudOn.Click += async (_, _) => await HudAsync(true);
        _hudOff.Click += async (_, _) => await HudAsync(false);
        _select.Click += async (_, _) => await ChooseValues();
        _install.Click += async (_, _) => await InstallHudAsync();
        // Two button groups (monitoring / display + HUD) that wrap as a whole when the window is narrow
        var monitor = Ui.Row(_start, _stop, _csv, _openLogs);
        var display = Ui.Row(_select, _hudOn, _hudOff, _install);
        monitor.Margin = new Thickness(0, 0, 16, 6);
        display.Margin = new Thickness(0, 0, 0, 6);
        var row = new WrapPanel { Children = { monitor, display } };
        _status.Margin = new Thickness(0, 6, 0, 0);
        _sources.Margin = new Thickness(0, 2, 0, 0);
        _sources.Foreground = AppTheme.Muted;
        top.SetContent(hint);
        top.SetContent(row);
        top.SetContent(_status);
        top.SetContent(_sources);

        _empty.Margin = new Thickness(4, 12, 0, 0);
        _tiles.Children.Add(_empty);
        var chartCard = _chartCard = new Card("Verlauf (letzte 2 Minuten)", "History (last 2 minutes)") { Margin = new Thickness(0, 12, 0, 0) };
        chartCard.SetContent(_chart);

        SetDock(top, Dock.Top);
        SetDock(chartCard, Dock.Bottom);
        Children.Add(top);
        Children.Add(chartCard);
        Children.Add(new ScrollViewer { Content = _tiles, MinHeight = 160, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        FitChart();
    }

    /// <summary>Chart height follows the number of graphs.</summary>
    private void FitChart()
    {
        if (_chartCard is null) return;
        int rows = Math.Max(1, _graphs.Count);
        _chartCard.IsVisible = _graphs.Count > 0;
        _chart.Height = Math.Clamp(60 + rows * 84, 160, 600) - 50;
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
        bool ready = _state.HasAdb && _state.SelectedDevice is { IsReady: true };
        _start.IsEnabled = ready && !Running;
        _stop.IsEnabled = Running;
        _hudOn.IsVisible = _hudOff.IsVisible = _hudPackage is not null;
        _hudOn.IsEnabled = _hudOff.IsEnabled = ready && _hudPackage is not null;
        _install.IsVisible = _installKind is not null;
        _install.IsEnabled = ready && !_installing;
        _install.Content = _installKind == "pfd"
            ? Loc.T("PFD-Metrics auf dem Headset installieren", "Install PFD-Metrics on the headset")
            : Loc.T("OVR Metrics Tool (Meta Store) …", "OVR Metrics Tool (Meta Store) …");
        if (!Running)
            _status.Text = !_state.HasAdb
                ? Loc.T("ADB ist nicht eingerichtet (Tab „Allgemeine Einstellungen“).", "ADB is not set up (\"General settings\" tab).")
                : _state.SelectedDevice is { IsReady: true } d
                    ? Loc.T($"Bereit: {d.DisplayName} ({d.ShownSerial})", $"Ready: {d.DisplayName} ({d.ShownSerial})")
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
        if (_csv.IsChecked == true) OpenCsv();
        _tick.Start();
        _status.Text = Loc.T($"Messung läuft: {device.DisplayName} ({device.ShownSerial})", $"Monitoring: {device.DisplayName} ({device.ShownSerial})");
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
        if (was)
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
                tile.Click += () => ToggleGraph(m.Key);
                _tileMap[m.Key] = tile;
                added = true;
            }
            tile.SetValue(has ? v : double.NaN);
        }
        if (added || (_tiles.Children.Contains(_empty) && _tileMap.Keys.Any(k => !_hidden.Contains(k))))
            RebuildTiles();
    }

    private void RebuildTiles()
    {
        _tiles.Children.Clear();
        foreach (PerfMetric m in PerfMetrics.All)
            if (!_hidden.Contains(m.Key) && _tileMap.TryGetValue(m.Key, out MetricTile? t)) _tiles.Children.Add(t);
        if (_tiles.Children.Count == 0) _tiles.Children.Add(_empty);
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

    private async Task ChooseValues()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!await MetricSelectDialog.Show(owner, _hidden, _graphs, MaxGraphs, _serial is null ? new Dictionary<string, double>() : _lastSnapshot, _deviceKind, _hudPackage == PfdHud)) return;
        RebuildTiles();
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
        if (_csvFile is not null)
            _sources.Text = Loc.T($"CSV gespeichert: {Privacy.MaskPaths(_csvFile)}", $"CSV saved: {Privacy.MaskPaths(_csvFile)}");
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
    private const string PfdHud = PfdMetricsInstaller.HudPackage;
    private const string OvrMetrics = "com.oculus.ovrmonitormetricsservice";
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

    /// <summary>PFD-Metrics: install the latest release directly via ADB (Windows and Linux). Quest: open the Meta Store page.</summary>
    private async Task InstallHudAsync()
    {
        AdbDevice? device = _state.SelectedDevice;
        if (_installKind is null || device is not { IsReady: true } || _installing) return;
        if (_installKind == "quest")
        {
            Platform.Open(OvrStoreUrl);
            _status.Text = Loc.T("Meta Store geöffnet – OVR Metrics Tool dort für dein Konto „holen“; danach im Headset installieren.",
                                 "Meta Store opened – \"get\" the OVR Metrics Tool for your account there, then install it on the headset.");
            return;
        }
        if (!_state.TryBeginOperation(Loc.T("PFD-Metrics wird installiert", "Installing PFD-Metrics")))
            return;

        _installing = true;
        UpdateEnabled();
        try
        {
            string tag = await PfdMetricsInstaller.InstallAsync(new AdbClient(_state.AdbPath!, device.Serial), s => _status.Text = s, CancellationToken.None);
            _status.Text = Loc.T($"PFD-Metrics {tag} installiert. Im Headset Monitoring einschalten und einmal den Zugriff auf Geräteprotokolle erlauben.",
                                 $"PFD-Metrics {tag} installed. Enable monitoring in the headset and allow access to device logs once.");
        }
        catch (Exception ex)
        {
            _status.Text = Loc.T("Installation fehlgeschlagen: ", "Installation failed: ") + ex.Message;
        }
        finally
        {
            _installing = false;
            _state.EndOperation();
            await DetectHudAsync();
        }
    }
}
