using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AdbTool.Cast;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

internal sealed class ConnectionLostException : Exception
{
    public double AverageMbps { get; init; }
    public double Elapsed { get; init; }
    public long Sent { get; init; }

    public ConnectionLostException(string message) : base(message) { }
}

/// <summary>
/// Tab 3: ADB/USB throughput test (former ADB USB Speed Test).
/// PC TCP sender → adb reverse → USB → "toybox nc" on the device → /dev/null.
/// </summary>
internal sealed class SpeedTestPage : DockPanel, IPage
{
    private const int ChunkSize = 1024 * 1024;
    private const int MaxRetries = 8;
    private const double BackoffFactor = 0.90;
    private const double MinRetryMbps = 50.0;

    private readonly AppState _state;

    private readonly TextBox _port = Ui.TextBox(110);
    private readonly RadioButton _limited = Ui.Radio("Bandbreite begrenzen", "Limit bandwidth");
    private readonly RadioButton _max = Ui.Radio("Maximale stabile Transferrate ermitteln", "Determine maximum stable transfer rate");
    private readonly TextBox _rate = Ui.TextBox(110);
    private readonly RadioButton _pathAdb = Ui.Radio("ADB (adb reverse, über USB oder WLAN-ADB)", "ADB (adb reverse, via USB or Wi-Fi ADB)");
    private readonly RadioButton _pathDirect = Ui.Radio("TCP/IP direkt (USB-NCM, Ethernet oder WLAN)", "TCP/IP direct (USB NCM, Ethernet or Wi-Fi)");
    private readonly ComboBox _directTarget = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 120, Margin = new Thickness(0, 4, 8, 4) };
    private readonly Button _detect = Ui.Button("Erkennen", "Detect");
    private readonly List<DirectTarget> _targets = new();
    private static readonly int[] StreamCounts = { 1, 4, 8 };
    private readonly ComboBox _streams = Ui.Combo(80);
    private readonly TextBlock _resultDownValue = Live();
    private readonly TextBlock _resultUpValue = Live();
    private readonly List<TcpClient> _directClients = new();
    private bool _proxApplied;
    private UsbNetworkHold? _ncmHold;
    private readonly SpeedChart _chart = new();
    private readonly TextBlock _analysis = Live();
    private readonly Button _export = Ui.Button("Bericht speichern (PNG + CSV)", "Save report (PNG + CSV)");
    private readonly Stopwatch _testClock = new();
    private int _phase;
    private string _firmware = "";
    private string _testDescription = "";
    private CancellationTokenSource? _wifiCts;
    private double _lastAnalysisT;
    private bool _detecting;
    private string? _targetsSerial;
    private const int StallTimeoutMs = 10_000;

    /// <summary>Address of the headset for the direct TCP test (IPv6 link-local with the PC's interface as scope).</summary>
    private sealed record DirectTarget(string Name, IPAddress Address, TargetKind Kind)
    {
        public bool Wifi => Kind == TargetKind.Wifi;
    }

    /// <summary>
    /// The direct test covers two different set-ups (plus Wi-Fi):
    /// USB NCM = network over the USB cable itself, PC ↔ headset, IPv6 link-local (no IPv4 / DHCP – that is normal);
    /// USB Ethernet adapter = adapter on the headset, cable to the router, PC ↔ router ↔ headset over IPv4.
    /// </summary>
    private enum TargetKind { UsbNcm, EthernetAdapter, Wifi }

    private static string KindText(TargetKind kind) => kind switch
    {
        TargetKind.UsbNcm => Loc.T("USB-NCM über USB-Kabel", "USB NCM over the USB cable"),
        TargetKind.EthernetAdapter => Loc.T("USB-Ethernet-Adapter über Router", "USB Ethernet adapter via router"),
        _ => Loc.T("WLAN über Router", "Wi-Fi via router"),
    };
    private readonly TextBox _duration = Ui.TextBox(110);

    private readonly Button _check = Ui.Button("Gerät prüfen", "Check device");
    private readonly Button _start = Ui.Button("Start", "Start", ButtonKind.Primary);
    private readonly Button _cancel = Ui.Button("Abbrechen", "Cancel", ButtonKind.Danger);

    private readonly TextBlock _hmdValue = Ui.Value();
    private readonly TextBlock _statusValue = Ui.Value();
    private readonly TextBlock _attemptValue = Live();
    private readonly TextBlock _targetValue = Live();
    private readonly TextBlock _elapsedValue = Live();
    private readonly TextBlock _remainingValue = Live();
    private readonly TextBlock _sentValue = Live();
    private readonly TextBlock _currentValue = Live();
    private readonly TextBlock _averageValue = Live();
    private readonly TextBlock _lastDisconnectValue = Ui.Value();
    private readonly ProgressBar _progress = new() { Maximum = 1000, Height = 8 };
    private readonly TextBox _history = Ui.Console();

    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private TcpClient? _client;
    private Process? _ncProcess;
    private AdbClient? _adb;

    // Live statistics written by the sender thread, read by the UI timer.
    private long _liveSent;
    private double _liveElapsed;
    private double _liveDuration;
    private bool _liveRunning;
    private double _lastUiTime;
    private long _lastUiBytes;

    private bool _hmdChecked;
    private string _hmdModel = "";
    private (string De, string En) _status = ("Bereit", "Ready");

    /// <summary>Value that changes several times per second: single line, no wrapping.</summary>
    private static TextBlock Live() => new() { Text = "-", Margin = new Thickness(0, 4, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis };

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    public SpeedTestPage(AppState state)
    {
        _state = state;
        Margin = new Thickness(20, 16);

        BuildUi();

        _port.Text = _state.Settings.SpeedPort.ToString(CultureInfo.InvariantCulture);
        _rate.Text = _state.Settings.SpeedRateMbps.ToString("0.###", CultureInfo.InvariantCulture);
        _duration.Text = _state.Settings.SpeedDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        _max.IsChecked = true; // always start in max-speed mode (as before)
        UpdateModeState();
        ResetStats(ParseDouble(_duration.Text, 600));

        _uiTimer.Tick += (_, _) => PublishLiveStats();
        _state.BusyChanged += UpdateEnabled;
        DetachedFromVisualTree += (_, _) => _ncmHold?.Dispose();
        _state.SelectedDeviceChanged += () =>
        {
            if (_ncmHold is not null && _ncmHold.Serial != _state.SelectedDevice?.Serial) _ = ReleaseNcmAsync();
            if (_cts is null) { _hmdChecked = false; RenderHmd(); }
            if (_targetsSerial != _state.SelectedDevice?.Serial) ClearTargets();
            UpdateEnabled();
            AutoDetect();
        };
        Loc.LanguageChanged += () => { RenderHmd(); _statusValue.Text = Loc.T(_status.De, _status.En); };
        // After the start the device is often reported as ready only a little later
        _state.DevicesChanged += () => Dispatcher.UIThread.Post(AutoDetect);
        UpdateEnabled();
    }

    private void BuildUi()
    {
        // Settings card -----------------------------------------------------
        var settings = new Card("Verbindung / Testeinstellungen", "Connection / Test settings");
        var grid = Ui.Grid(2);
        Ui.AddRow(grid, Ui.Label("TCP-Port:", "TCP port:", muted: true), Ui.Row(_port));

        _pathAdb.GroupName = _pathDirect.GroupName = "speedPath";
        var paths = new StackPanel { Margin = new Thickness(0, 4, 0, 2), Children = { _pathAdb, _pathDirect } };
        _pathAdb.IsChecked = !_state.Settings.SpeedDirect;
        _pathDirect.IsChecked = _state.Settings.SpeedDirect;
        _pathDirect.IsCheckedChanged += async (_, _) =>
        {
            _state.Settings.SpeedDirect = _pathDirect.IsChecked == true;
            _state.Settings.Save();
            if (_pathDirect.IsChecked != true) await ReleaseNcmAsync();
            UpdateEnabled();
            AutoDetect();
        };
        Ui.AddRow(grid, Ui.Label("Messweg:", "Test path:", muted: true), paths);
        _detect.Click += async (_, _) => await DetectTargetsAsync();
        _directTarget.ItemsSource = new[] { Loc.T("– noch nicht erkannt –", "– not detected yet –") };
        _directTarget.SelectedIndex = 0;
        _directTarget.SelectionChanged += (_, _) => UpdateEnabled();
        var targetRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        Grid.SetColumn(_detect, 1);
        _detect.Margin = default;
        _detect.VerticalAlignment = VerticalAlignment.Center;
        targetRow.Children.Add(_directTarget);
        targetRow.Children.Add(_detect);
        Ui.AddRow(grid, Ui.Label("Ziel:", "Target:", muted: true), targetRow);
        _streams.ItemsSource = StreamCounts.Select(n => n.ToString(CultureInfo.InvariantCulture)).ToList();
        _streams.SelectedIndex = Math.Max(0, Array.IndexOf(StreamCounts, _state.Settings.SpeedStreams));
        _streams.SelectionChanged += (_, _) => { _state.Settings.SpeedStreams = StreamCounts[Math.Max(0, _streams.SelectedIndex)]; _state.Settings.Save(); };
        var streamsHint = Ui.Label("parallele TCP-Verbindungen (mehrere = vergleichbar mit Online-Speedtests)", "parallel TCP connections (several = comparable to online speed tests)", muted: true);
        var streamsRow = new DockPanel();
        DockPanel.SetDock(_streams, Dock.Left);
        streamsRow.Children.Add(_streams);
        streamsRow.Children.Add(streamsHint);
        Ui.AddRow(grid, Ui.Label("Verbindungen:", "Connections:", muted: true), streamsRow);

        _max.GroupName = _limited.GroupName = "speedMode";
        var modes = new StackPanel { Margin = new Thickness(0, 4, 0, 4), Children = { _max, _limited } };
        _limited.IsCheckedChanged += (_, _) => { UpdateModeState(); SaveSettings(); };
        _max.IsCheckedChanged += (_, _) => { UpdateModeState(); SaveSettings(); };
        Ui.AddRow(grid, Ui.Label("Modus:", "Mode:", muted: true), modes);

        Ui.AddRow(grid, Ui.Label("Zielrate:", "Target rate:", muted: true), Ui.Row(_rate, Ui.Value("Mbit/s")));
        Ui.AddRow(grid, Ui.Label("Testdauer:", "Test duration:", muted: true), Ui.Row(_duration, Ui.Label("Sekunden pro Versuch", "seconds per attempt", muted: true)));
        foreach (TextBox box in new[] { _port, _rate, _duration })
            box.LostFocus += (_, _) => SaveSettings();

        _check.Click += async (_, _) => await CheckHmdAsync();
        _start.Click += async (_, _) => await StartTestAsync();
        _cancel.Click += (_, _) => CancelOperation();
        var buttons = Ui.Row(_check, _start, _cancel);
        buttons.Margin = new Thickness(0, 12, 0, 8);
        grid.AddRow(buttons);

        var info = Ui.Label(
            "Max-Speed-Modus: Der erste Versuch läuft ohne Limit. Wird die Verbindung getrennt, baut das Tool ADB/Netcat automatisch neu auf und reduziert die Datenrate schrittweise, bis ein kompletter Test stabil durchläuft.",
            "Max-Speed mode: The first attempt runs without a bandwidth limit. If the connection is lost, the tool automatically rebuilds ADB/Netcat and gradually reduces the transfer rate until a complete test runs stably.",
            muted: true);
        info.Margin = new Thickness(0, 6, 0, 0);
        grid.AddRow(info);

        var how = Ui.Label(
            "Messpfad ADB: PC-TCP-Sender → adb reverse → USB/WLAN → toybox nc → /dev/null. Der Wert ist der Durchsatz dieses ADB/TCP-Pfads, nicht die theoretische USB-Busgeschwindigkeit. " +
            "TCP/IP direkt (ohne ADB, beide Richtungen) – zwei verschiedene Messungen: " +
            "① USB-NCM: Netzwerk direkt über das USB-Kabel zwischen PC und Headset, per IPv6 link-local (eine IPv4-Adresse gibt es dabei normalerweise nicht). Bei der Quest schaltet ADBora es über die Begleit-App ein, bei der Play For Dream in den experimentellen Einstellungen aktivieren. " +
            "② USB-Ethernet-Adapter am Headset, per Kabel am Router: Messung PC ↔ Router ↔ Headset über IPv4 – zeigt, was das Heimnetz schafft. " +
            "Dazu WLAN über den Router zum Vergleich. Es wird nichts auf dem Gerät gespeichert.",
            "ADB path: PC TCP sender → adb reverse → USB/WLAN → toybox nc → /dev/null. The value is the throughput of this ADB/TCP path, not the theoretical USB bus speed. " +
            "TCP/IP direct (without ADB, both directions) – two different measurements: " +
            "① USB NCM: network directly over the USB cable between PC and headset, via IPv6 link-local (there is normally no IPv4 address). On the Quest ADBora switches it on via the companion app, on the Play For Dream enable it in the experimental settings. " +
            "② USB Ethernet adapter on the headset, cable to the router: measures PC ↔ router ↔ headset over IPv4 – shows what the home network achieves. " +
            "Plus Wi-Fi via the router for comparison. Nothing is written to the device.", muted: true);
        how.Margin = new Thickness(0, 10, 0, 0);
        grid.AddRow(how);
        settings.SetContent(grid);

        // Status card ---------------------------------------------------------
        var status = new Card("Status", "Status");
        var statusGrid = Ui.Grid(2);
        statusGrid.ColumnDefinitions[0].Width = new GridLength(150);
        void Row(string de, string en, TextBlock value) => Ui.AddRow(statusGrid, Ui.Label(de, en, muted: true), value);
        Row("Gerät / HMD:", "Device / HMD:", _hmdValue);
        Row("Status:", "Status:", _statusValue);
        Row("Versuch:", "Attempt:", _attemptValue);
        Row("Aktuelles Ziel:", "Current target:", _targetValue);
        Row("Laufzeit:", "Elapsed:", _elapsedValue);
        Row("Verbleibend:", "Remaining:", _remainingValue);
        Row("Gesendet:", "Transferred:", _sentValue);
        Row("Aktuell:", "Current:", _currentValue);
        Row("Durchschnitt:", "Average:", _averageValue);
        Row("Letzter Abbruch:", "Last disconnect:", _lastDisconnectValue);
        Row("↓ Headset → PC:", "↓ Headset → PC:", _resultDownValue);
        Row("↑ PC → Headset:", "↑ PC → headset:", _resultUpValue);
        foreach (TextBlock big in new[] { _currentValue, _averageValue, _resultDownValue, _resultUpValue })
        {
            big.FontSize = AppTheme.SectionSize;
            big.FontWeight = FontWeight.SemiBold;
        }
        _averageValue.Foreground = AppTheme.Accent;
        _progress.Margin = new Thickness(0, 12, 0, 4);
        statusGrid.AddRow(_progress);
        status.SetContent(statusGrid);

        var root = Responsive.TwoColumns(settings, status, breakpoint: 940, padding: default, firstPercent: 46);

        // Throughput chart -----------------------------------------------------
        var chartCard = new Card("Durchsatz-Verlauf", "Throughput history");
        _analysis.Text = Loc.T("Einbrüche (unter 50 % des Medians) gelb, Aussetzer rot markiert.", "Dips (below 50 % of the median) marked yellow, stalls red.");
        _analysis.Foreground = AppTheme.Muted;
        _export.IsEnabled = false;
        _export.Click += async (_, _) => await ExportReport();
        _export.Margin = default;
        var chartTop = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_export, Dock.Right);
        chartTop.Children.Add(_export);
        chartTop.Children.Add(_analysis);
        _chart.Height = 260;
        chartCard.SetContent(chartTop);
        chartCard.SetContent(_chart);

        // History ------------------------------------------------------------
        var historyCard = new Card("Protokoll (diese Sitzung)", "Log (this session)");
        _history.Height = 180;
        historyCard.SetContent(_history);
        historyCard.Margin = default;

        Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Children = { root, chartCard, historyCard } },
        });
    }

    // ------------------------------------------------------------------

    public void OnActivated()
    {
        UpdateEnabled();
        AutoDetect();
    }

    /// <summary>
    /// "TCP/IP direct" selected (also right after the start, when the setting was saved that
    /// way): detect the targets automatically as soon as the tab is visible and the device is
    /// ready, once per device.
    /// </summary>
    private void AutoDetect()
    {
        if (_pathDirect.IsChecked != true || _cts is not null || _detecting || !IsEffectivelyVisible) return;
        if (_state.SelectedDevice is not { IsReady: true } d || !_state.HasAdb || _state.IsBusy) return;
        if (_targets.Count > 0 && _targetsSerial == d.Serial) return;
        _ = DetectTargetsAsync();
    }

    private void ClearTargets()
    {
        _targets.Clear();
        _targetsSerial = null;
        _directTarget.ItemsSource = new[] { Loc.T("– noch nicht erkannt –", "– not detected yet –") };
        _directTarget.SelectedIndex = 0;
    }

    private void UpdateModeState() => _rate.IsEnabled = _max.IsChecked != true;

    private void UpdateEnabled()
    {
        bool running = _cts is not null;
        bool idle = !_state.IsBusy;
        bool ready = _state.HasAdb && _state.SelectedDevice?.IsReady == true;
        _check.IsEnabled = idle && ready;
        _start.IsEnabled = idle && ready;
        _cancel.IsEnabled = running && !_cts!.IsCancellationRequested;
        bool direct = _pathDirect.IsChecked == true;
        _port.IsEnabled = _duration.IsEnabled = _pathAdb.IsEnabled = _pathDirect.IsEnabled = !running;
        _max.IsEnabled = _limited.IsEnabled = !running && !direct;
        _rate.IsEnabled = !running && _max.IsChecked != true && !direct;
        _directTarget.IsEnabled = _streams.IsEnabled = !running && direct;
        _detect.IsEnabled = idle && ready && direct && !_detecting;
        if (direct) _start.IsEnabled = idle && ready && SelectedTarget is not null;
    }

    private void SetStatus(string de, string en)
    {
        _status = (de, en);
        _statusValue.Text = Loc.T(de, en);
    }

    private void RenderHmd()
    {
        if (_hmdChecked)
            _hmdValue.Text = $"{Loc.T("Verbunden", "Connected")}: {_hmdModel}";
        else
            _hmdValue.Text = Loc.T("Nicht geprüft", "Not checked");
        _hmdValue.Foreground = _hmdChecked ? AppTheme.Accent : AppTheme.Text;
    }

    private void SaveSettings()
    {
        if (int.TryParse(_port.Text, out int port) && port is > 0 and < 65536)
            _state.Settings.SpeedPort = port;
        double rate = ParseDouble(_rate.Text, -1);
        if (rate > 0) _state.Settings.SpeedRateMbps = rate;
        double duration = ParseDouble(_duration.Text, -1);
        if (duration > 0) _state.Settings.SpeedDurationSeconds = duration;
        _state.Settings.Save();
    }

    private static double ParseDouble(string? text, double fallback) =>
        text is null ? fallback :
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;

    private async Task<(bool Ok, int Port, double Duration, double Rate)> ValidateInputs()
    {
        int port; double duration, rate = 0;

        if (!_state.HasAdb)
        {
            await Dialogs.Message(Owner, Loc.T("ADB ist nicht eingerichtet. Bitte im Tab „Allgemeine Einstellungen“ adb.exe auswählen.",
                "ADB is not set up. Please choose adb.exe in the \"General settings\" tab."), "ADBora", MessageKind.Error);
            return default;
        }
        if (_state.SelectedDevice is not { IsReady: true })
        {
            await Dialogs.Message(Owner, Loc.T("Bitte ein verbundenes, autorisiertes Gerät auswählen.", "Please select a connected, authorized device."), "ADBora");
            return default;
        }
        if (!int.TryParse(_port.Text, out port) || port is < 1 or > 65535)
        {
            await Dialogs.Message(Owner, Loc.T("Ungültiger TCP-Port.", "Invalid TCP port."), "ADBora");
            return default;
        }
        duration = ParseDouble(_duration.Text, -1);
        if (duration <= 0)
        {
            await Dialogs.Message(Owner, Loc.T("Ungültige Testdauer.", "Invalid test duration."), "ADBora");
            return default;
        }
        if (_limited.IsChecked == true)
        {
            rate = ParseDouble(_rate.Text, -1);
            if (rate <= 0)
            {
                await Dialogs.Message(Owner, Loc.T("Ungültige Zielrate.", "Invalid target rate."), "ADBora");
                return default;
            }
        }

        SaveSettings();
        return (true, port, duration, rate);
    }

    // ------------------------------------------------------------------

    private async Task CheckHmdAsync()
    {
        if (!(await ValidateInputs()).Ok)
            return;
        if (!_state.TryBeginOperation(Loc.T("Gerät wird geprüft", "Checking device")))
            return;

        _adb = _state.DeviceAdb!; // validated: device ready
        _hmdValue.Text = Loc.T("Prüfe...", "Checking...");
        try
        {
            _hmdModel = await GetModelAsync(CancellationToken.None);
            _hmdChecked = true;
            RenderHmd();
            SetStatus("Bereit", "Ready");
        }
        catch (Exception ex)
        {
            _hmdChecked = false;
            _hmdValue.Text = Loc.T($"Nicht verbunden: {ex.Message}", $"Not connected: {ex.Message}");
            _hmdValue.Foreground = AppTheme.Error;
        }
        finally
        {
            _state.EndOperation();
        }
    }

    /// <summary>Diagnostic (--ui-test speed): runs a test like the Start button and returns the log and the analysis.</summary>
    internal async Task<string> AutoTestAsync(double seconds, bool direct)
    {
        _duration.Text = seconds.ToString(CultureInfo.InvariantCulture);
        _pathDirect.IsChecked = direct;
        _pathAdb.IsChecked = !direct;
        _max.IsChecked = true;
        if (direct)
        {
            // The page starts its own detection (path switch, device list update): wait until one has finished
            var clock = Stopwatch.StartNew();
            while (_targetsSerial is null && clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (!_detecting) await DetectTargetsAsync();
                await Task.Delay(300);
            }
            ComboBoxLog($"targets: {string.Join(" | ", _targets.Select(t => t.Name))} · selected: {SelectedTarget?.Name ?? "-"}");
            if (SelectedTarget is null)
                return (_history.Text ?? "") + "\nNO TARGET · status: " + _statusValue.Text;
        }
        await StartTestAsync();
        return (_history.Text ?? "") + "\nanalysis: " + _analysis.Text + "\nstatus: " + _statusValue.Text + "\ndevice: " + _hmdValue.Text;
    }

    private void ComboBoxLog(string text) => Ui.AppendLine(_history, text);

    private async Task StartTestAsync()
    {
        if (_cts is not null) return;
        var (ok, port, duration, rate) = await ValidateInputs();
        if (!ok) return;
        if (!_state.TryBeginOperation(Loc.T("Speedtest läuft", "Speed test running")))
            return;

        _adb = _state.DeviceAdb!; // validated: device ready
        _cts = new CancellationTokenSource();
        UpdateEnabled();
        ResetStats(duration);

        bool max = _max.IsChecked == true;
        bool direct = _pathDirect.IsChecked == true;
        string result = "";

        try
        {
            _hmdModel = await GetModelAsync(_cts.Token);
            _hmdChecked = true;
            RenderHmd();
            await KeepAwakeAsync(true);
            _firmware = await GetFirmwareAsync();
            _chart.Clear();
            _export.IsEnabled = false;
            _lastAnalysisT = 0;
            _testClock.Restart();
            int streamsUsed = StreamCounts[Math.Max(0, _streams.SelectedIndex)];
            _testDescription = direct
                ? Loc.T($"Messweg: TCP/IP direkt · {SelectedTarget?.Name} · {streamsUsed} Verbindung(en) · {duration:0} s pro Richtung",
                        $"Test path: TCP/IP direct · {SelectedTarget?.Name} · {streamsUsed} connection(s) · {duration:0} s per direction")
                : Loc.T($"Messweg: ADB (adb reverse) über {(_adb.Serial.Contains(':') ? "WLAN-ADB" : "USB")} · {(max ? "Max" : $"Limit {rate:0} Mbit/s")} · {duration:0} s",
                        $"Test path: ADB (adb reverse) via {(_adb.Serial.Contains(':') ? "Wi-Fi ADB" : "USB")} · {(max ? "max" : $"limit {rate:0} Mbit/s")} · {duration:0} s");
            if ((direct && SelectedTarget?.Wifi == true) || (!direct && _adb.Serial.Contains(':')))
                StartWifiPolling(_adb);

            result = direct
                ? await RunDirectAsync(SelectedTarget!, port, duration, _cts.Token)
                : max
                    ? await RunMaxModeAsync(port, duration, _cts.Token)
                    : await RunLimitedModeAsync(port, duration, rate, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Abgebrochen", "Cancelled");
            result = Loc.T("abgebrochen", "cancelled");
        }
        catch (Exception ex)
        {
            SetStatus($"Fehler: {ex.Message}", $"Error: {ex.Message}");
            result = Loc.T("Fehler: ", "Error: ") + ex.Message;
            await Dialogs.Message(Owner, ex.Message, "ADBora", MessageKind.Error);
        }
        finally
        {
            _uiTimer.Stop();
            _liveRunning = false;
            _wifiCts?.Cancel();
            _wifiCts = null;
            CleanupConnection();
            await KeepAwakeAsync(false);
            if (_chart.Analyze() is { } analysis)
            {
                _analysis.Text = analysis.Summary(!Loc.IsGerman);
                Ui.AppendLine(_history, $"{DateTime.Now:HH:mm:ss}    {analysis.Summary(!Loc.IsGerman)}");
            }
            _export.IsEnabled = _chart.HasData;
            if (direct)
            {
                await StopDeviceNcAsync(port);
            }
            else
            {
                await RemoveReverseAsync(port);
                // Clean ADB state after a USB test, as in the stand-alone tool. Not for
                // Wi-Fi devices: stopping the server would drop the Wi-Fi connection.
                if (_adb is not null && !_adb.Serial.Contains(':'))
                    await _adb.KillServerAsync();
            }
            _cts.Dispose();
            _cts = null;
            _state.EndOperation();
            UpdateEnabled();

            string mode = direct ? $"{Loc.T("TCP direkt", "TCP direct")} · {SelectedTarget?.Name}" : max ? "Max" : $"{Loc.T("Limit", "Limit")} {rate:0} Mbit/s";
            Ui.AppendLine(_history, $"{DateTime.Now:HH:mm:ss}  {_hmdModel} ({_state.SelectedDevice?.ShownSerial})  ·  {mode}  ·  {duration:0} s  →  {result}");
            _ = _state.RefreshDevicesAsync();
        }
    }

    private async Task<string> RunLimitedModeAsync(int port, double duration, double rate, CancellationToken token)
    {
        _attemptValue.Text = "1";
        _targetValue.Text = $"{rate:0.0} Mbit/s";

        var (_, _, average) = await RunSingleAttemptAsync(port, duration, rate, 1, token);

        SetStatus($"Fertig – Durchschnitt: {average:0.0} Mbit/s", $"Finished – average: {average:0.0} Mbit/s");
        return $"{average:0.0} Mbit/s";
    }

    private async Task<string> RunMaxModeAsync(int port, double duration, CancellationToken token)
    {
        double? target = null;
        double lastFailed = 0;

        for (int attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            token.ThrowIfCancellationRequested();
            _attemptValue.Text = attempt.ToString(CultureInfo.InvariantCulture);

            if (target is null)
            {
                _targetValue.Text = Loc.T("Unbegrenzt", "Unlimited");
                SetStatus($"Versuch {attempt}: ermittle maximale Rohgeschwindigkeit...", $"Attempt {attempt}: determining maximum raw speed...");
            }
            else
            {
                _targetValue.Text = $"{target.Value:0.0} Mbit/s";
                SetStatus($"Versuch {attempt}: Stabilitätstest bei {target.Value:0.0} Mbit/s...", $"Attempt {attempt}: stability test at {target.Value:0.0} Mbit/s...");
            }

            try
            {
                var (_, _, average) = await RunSingleAttemptAsync(port, duration, target, attempt, token);
                _progress.Value = 1000;
                SetStatus($"Fertig – stabile Transferrate: {average:0.0} Mbit/s", $"Finished – stable transfer rate: {average:0.0} Mbit/s");
                return Loc.T($"stabil {average:0.0} Mbit/s", $"stable {average:0.0} Mbit/s");
            }
            catch (ConnectionLostException ex)
            {
                lastFailed = ex.AverageMbps;
                _lastDisconnectValue.Text = Loc.T($"{ex.AverageMbps:0.0} Mbit/s nach {ex.Elapsed:0.0} s", $"{ex.AverageMbps:0.0} Mbit/s after {ex.Elapsed:0.0} s");

                if (ex.AverageMbps <= 0)
                    throw new Exception(Loc.T("Die Verbindung wurde getrennt, bevor eine brauchbare Geschwindigkeit gemessen werden konnte.",
                        "The connection was lost before a usable speed could be measured."));

                target = target is null
                    ? ex.AverageMbps * BackoffFactor
                    : Math.Min(target.Value * BackoffFactor, ex.AverageMbps * BackoffFactor);
                target = Math.Max(target.Value, MinRetryMbps);

                if (attempt > MaxRetries)
                    throw new Exception(Loc.T(
                        $"Auch nach mehreren automatischen Reduzierungen konnte keine stabile Transferrate gefunden werden. Letzter Abbruch bei durchschnittlich {lastFailed:0.0} Mbit/s.",
                        $"No stable transfer rate could be found after several automatic reductions. Last disconnect at an average of {lastFailed:0.0} Mbit/s."));

                SetStatus($"Verbindung bei {ex.AverageMbps:0.0} Mbit/s abgebrochen. Neuer Versuch mit {target.Value:0.0} Mbit/s...",
                    $"Connection lost at {ex.AverageMbps:0.0} Mbit/s. Retrying at {target.Value:0.0} Mbit/s...");

                CleanupConnection();
                await EnsureAdbReadyAsync(token, 20);
                await Task.Delay(500, token);
            }
        }

        throw new Exception(Loc.T("Kein Ergebnis.", "No result."));
    }

    private async Task<(double Elapsed, long Sent, double AverageMbps)> RunSingleAttemptAsync(
        int port, double duration, double? targetMbps, int attemptNo, CancellationToken token)
    {
        TcpClient client = await PrepareConnectionAsync(port, token);

        if (targetMbps is null)
            SetStatus($"Versuch {attemptNo}: sende ohne Bandbreitenlimit...", $"Attempt {attemptNo}: sending without bandwidth limit...");
        else
            SetStatus($"Versuch {attemptNo}: teste {targetMbps.Value:0.0} Mbit/s...", $"Attempt {attemptNo}: testing {targetMbps.Value:0.0} Mbit/s...");

        _progress.Value = 0;
        _phase = _chart.AddPhase(Loc.T($"ADB · Versuch {attemptNo}", $"ADB · attempt {attemptNo}") + (targetMbps is null ? "" : $" · {targetMbps.Value:0} Mbit/s"));
        Interlocked.Exchange(ref _liveSent, 0);
        Volatile.Write(ref _liveElapsed, 0);
        _liveDuration = duration;
        _lastUiTime = 0;
        _lastUiBytes = 0;
        _liveRunning = true;
        _uiTimer.Start();

        try
        {
            var result = await Task.Run(() => SendLoop(client.Client, duration, targetMbps, token), token);
            ForceStats(result.Elapsed, duration, result.Sent, result.AverageMbps);
            return result;
        }
        finally
        {
            _liveRunning = false;
            _uiTimer.Stop();
        }
    }

    /// <summary>Runs on a worker thread: blocking sends, optional rate limiting.</summary>
    private (double Elapsed, long Sent, double AverageMbps) SendLoop(Socket socket, double duration, double? targetMbps, CancellationToken token)
    {
        byte[] payload = new byte[ChunkSize];
        Array.Fill(payload, (byte)'X');
        double? rateBytes = targetMbps is null ? null : targetMbps.Value * 1_000_000.0 / 8.0;
        long sent = 0;
        Stopwatch sw = Stopwatch.StartNew();

        while (sw.Elapsed.TotalSeconds < duration)
        {
            token.ThrowIfCancellationRequested();
            double elapsed = sw.Elapsed.TotalSeconds;
            Volatile.Write(ref _liveElapsed, elapsed);

            if (rateBytes is not null)
            {
                double targetBytes = elapsed * rateBytes.Value;
                if (sent > targetBytes)
                {
                    double waitSec = Math.Min((sent - targetBytes) / rateBytes.Value, 0.01);
                    if (waitSec > 0)
                        Thread.Sleep(TimeSpan.FromSeconds(waitSec));
                    continue;
                }
            }

            try
            {
                int n = socket.Send(payload, 0, payload.Length, SocketFlags.None);
                if (n == 0)
                    throw new SocketException((int)SocketError.ConnectionReset);
                sent += n;
                Interlocked.Exchange(ref _liveSent, sent);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                token.ThrowIfCancellationRequested();
                double e = Math.Max(sw.Elapsed.TotalSeconds, 0.000001);
                Volatile.Write(ref _liveElapsed, e);
                throw new ConnectionLostException(ex.Message)
                {
                    AverageMbps = sent * 8.0 / e / 1_000_000.0,
                    Elapsed = e,
                    Sent = sent
                };
            }
        }

        double final = Math.Max(sw.Elapsed.TotalSeconds, 0.000001);
        return (final, sent, sent * 8.0 / final / 1_000_000.0);
    }

    private void PublishLiveStats()
    {
        if (!_liveRunning) return;
        double elapsed = Volatile.Read(ref _liveElapsed);
        long sent = Interlocked.Read(ref _liveSent);
        double interval = elapsed - _lastUiTime;
        double current = interval > 0 ? (sent - _lastUiBytes) * 8.0 / interval / 1_000_000.0 : 0;
        if (interval <= 0) return;
        ForceStats(elapsed, _liveDuration, sent, current);
        _lastUiTime = elapsed;
        _lastUiBytes = sent;
        double t = _testClock.Elapsed.TotalSeconds;
        _chart.Add(new RateSample(t, current, _phase));
        if (t - _lastAnalysisT >= 1)
        {
            _lastAnalysisT = t;
            if (_chart.Analyze() is { } a) _analysis.Text = a.Summary(!Loc.IsGerman);
        }
    }

    private void ForceStats(double elapsed, double duration, long sent, double current)
    {
        double remaining = Math.Max(duration - elapsed, 0);
        double average = elapsed > 0 ? sent * 8.0 / elapsed / 1_000_000.0 : 0;
        double gib = sent / Math.Pow(1024, 3);
        int progress = duration > 0 ? (int)Math.Clamp(elapsed / duration * 1000.0, 0, 1000) : 0;

        SetText(_elapsedValue, $"{elapsed:0.0} s");
        SetText(_remainingValue, $"{remaining:0.0} s");
        SetText(_sentValue, $"{gib:0.00} GiB");
        SetText(_currentValue, $"{current:0.0} Mbit/s");
        SetText(_averageValue, $"{average:0.0} Mbit/s");
        _progress.Value = progress;
    }

    /// <summary>Only touches the label if the text really changed (avoids needless repaints).</summary>
    private static void SetText(TextBlock label, string text)
    {
        if (label.Text != text)
            label.Text = text;
    }

    private async Task<TcpClient> PrepareConnectionAsync(int port, CancellationToken token)
    {
        CleanupConnection();

        SetStatus("Richte ADB-Reverse ein...", "Setting up ADB reverse...");
        await SetupReverseAsync(port, token);

        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();

        SetStatus("Starte Empfänger auf dem Gerät...", "Starting receiver on the device...");
        _ncProcess = _adb!.Start("shell", $"toybox nc 127.0.0.1 {port} > /dev/null");

        SetStatus("Warte auf Geräteverbindung...", "Waiting for device connection...");
        Task<TcpClient> accept = _listener.AcceptTcpClientAsync(token).AsTask();
        Task timeout = Task.Delay(TimeSpan.FromSeconds(10), token);

        if (await Task.WhenAny(accept, timeout) != accept)
        {
            token.ThrowIfCancellationRequested();
            throw new TimeoutException(Loc.T("Timeout: Das Gerät hat keine Testverbindung aufgebaut.", "Timeout: The device did not establish the test connection."));
        }

        _client = await accept;
        _client.NoDelay = true;
        _client.Client.SendBufferSize = 4 * 1024 * 1024;
        _client.Client.SendTimeout = StallTimeoutMs; // headset in standby: counts as a disconnect instead of hanging
        return _client;
    }

    private async Task<string> GetModelAsync(CancellationToken token)
    {
        await EnsureAdbReadyAsync(token, 10);
        AdbResult result = await _adb!.CaptureAsync(token, "shell", "getprop", "ro.product.model");
        if (!result.Ok)
            throw new Exception(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        return string.IsNullOrWhiteSpace(result.Output) ? "Android" : result.Output.Trim();
    }

    private async Task EnsureAdbReadyAsync(CancellationToken token, int timeoutSeconds)
    {
        SetStatus("ADB-Verbindung wird wiederhergestellt...", "Recovering ADB connection...");
        var server = new AdbClient(_adb!.Executable);
        try { await server.CaptureAsync(CancellationToken.None, "start-server"); } catch { }

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                AdbResult state = await _adb.CaptureAsync(token, "get-state");
                if (state.Ok && state.Output.Trim() == "device")
                {
                    await Task.Delay(400, token);
                    AdbResult verify = await _adb.CaptureAsync(token, "get-state");
                    if (verify.Ok && verify.Output.Trim() == "device")
                        return;
                }
                try { await _adb.CaptureAsync(token, "reconnect"); } catch (OperationCanceledException) { throw; } catch { }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            await Task.Delay(500, token);
        }

        throw new Exception(Loc.T("ADB-Gerät wurde nicht wieder online.", "ADB device did not come back online."));
    }

    private async Task SetupReverseAsync(int port, CancellationToken token)
    {
        await EnsureAdbReadyAsync(token, 20);
        try { await _adb!.CaptureAsync(CancellationToken.None, "reverse", "--remove", $"tcp:{port}"); } catch { }

        AdbResult result = await _adb!.CaptureAsync(token, "reverse", $"tcp:{port}", $"tcp:{port}");
        if (!result.Ok)
            throw new Exception(Loc.T($"ADB reverse fehlgeschlagen: {result.Error}", $"ADB reverse failed: {result.Error}"));
    }

    // ------------------------------------------------------------------
    // Direct TCP/IP test (USB NCM / Wi-Fi), both directions, without ADB in the data path
    // ------------------------------------------------------------------

    private DirectTarget? SelectedTarget =>
        _directTarget.SelectedIndex >= 0 && _directTarget.SelectedIndex < _targets.Count ? _targets[_directTarget.SelectedIndex] : null;

    /// <summary>
    /// Reads the headset's network interfaces ("ip -o addr"): Wi-Fi (IPv4) and USB networking
    /// (usb*/ncm*/rndis*). For USB the IPv4 address is used if the PC has an address in the same
    /// subnet, otherwise the IPv6 link-local address with the PC's USB network adapter as scope
    /// (works without DHCP).
    /// </summary>
    private async Task DetectTargetsAsync()
    {
        AdbDevice? device = _state.SelectedDevice;
        if (_detecting) return;
        if (device is not { IsReady: true } || !_state.HasAdb)
        {
            SetStatus("Kein bereites Gerät ausgewählt.", "No ready device selected.");
            return;
        }
        _detecting = true;
        _detect.IsEnabled = false;
        SetStatus("Suche Netzwerk-Schnittstellen...", "Looking for network interfaces...");
        var found = new List<DirectTarget>();
        try
        {
            var adb = new AdbClient(_state.AdbPath!, device.Serial);
            if (device.Model.Contains("Quest", StringComparison.OrdinalIgnoreCase))
                await EnsureQuestNcmAsync(adb, device.Serial);
            AdbResult r = await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "shell", "ip -o addr show");
            var v4 = new List<(string If, IPAddress Ip, int Prefix)>();
            var v6 = new List<(string If, IPAddress Ip)>();
            foreach (Match m in Regex.Matches(r.Output, @"^\d+:\s+(\S+)\s+inet\s+([\d.]+)/(\d+)", RegexOptions.Multiline))
                v4.Add((m.Groups[1].Value, IPAddress.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)));
            foreach (Match m in Regex.Matches(r.Output, @"^\d+:\s+(\S+)\s+inet6\s+(fe80:[0-9a-f:]+)/\d+", RegexOptions.Multiline))
                v6.Add((m.Groups[1].Value, IPAddress.Parse(m.Groups[2].Value)));

            static bool IsUsb(string name) => Regex.IsMatch(name, @"^(usb|ncm|rndis)\d*$");
            NetworkInterface[] pcNics = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).ToArray();

            // 1) USB NCM: network over the USB cable, always IPv6 link-local (scope = the PC's USB network adapter).
            //    An IPv4 address on the headset's usb0 is not used – the PC normally has none there.
            foreach (var (usbIf, ip6) in v6.Where(x => IsUsb(x.If)).GroupBy(x => x.If).Select(g => g.First()))
            {
                foreach (NetworkInterface nic in pcNics.Where(IsUsbAdapter))
                {
                    if (!nic.Supports(NetworkInterfaceComponent.IPv6)) continue;
                    var addr = new IPAddress(ip6.GetAddressBytes(), nic.GetIPProperties().GetIPv6Properties().Index);
                    found.Add(new DirectTarget($"{KindText(TargetKind.UsbNcm)} · IPv6 link-local ({usbIf} ↔ {nic.Name})", addr, TargetKind.UsbNcm));
                }
            }
            // 2) USB Ethernet adapter on the headset (e.g. RTL8156), cable to the router: IPv4 over the LAN
            foreach (var e in v4.Where(x => Regex.IsMatch(x.If, @"^eth\d+$")))
                found.Add(new DirectTarget($"{KindText(TargetKind.EthernetAdapter)} · {e.Ip} ({e.If})", e.Ip, TargetKind.EthernetAdapter));
            // 3) Wi-Fi via the router
            foreach (var w in v4.Where(x => x.If.StartsWith("wlan", StringComparison.Ordinal)))
                found.Add(new DirectTarget($"{KindText(TargetKind.Wifi)} · {w.Ip} ({w.If})", w.Ip, TargetKind.Wifi));
        }
        catch (Exception ex)
        {
            SetStatus($"Fehler: {ex.Message}", $"Error: {ex.Message}");
        }

        _targets.Clear();
        _targets.AddRange(found);
        _directTarget.ItemsSource = _targets.Count > 0
            ? _targets.Select(t => t.Name).ToList()
            : new List<string> { Loc.T("– keine Schnittstelle gefunden –", "– no interface found –") };
        if (_targets.Count == 0)
        {
            SetStatus("Keine Netzwerk-Schnittstelle gefunden.", "No network interface found.");
        }
        else
        {
            bool usb = _targets.Any(t => t.Kind == TargetKind.UsbNcm);
            bool eth = _targets.Any(t => t.Kind == TargetKind.EthernetAdapter);
            if (usb && eth) SetStatus("USB-NCM (USB-Kabel) und USB-Ethernet-Adapter (Router) gefunden – Ziel auswählen.", "USB NCM (USB cable) and USB Ethernet adapter (router) found – choose the target.");
            else if (usb) SetStatus("USB-NCM gefunden – Messung direkt über das USB-Kabel (IPv6 link-local).", "USB NCM found – measuring directly over the USB cable (IPv6 link-local).");
            else if (eth) SetStatus("USB-Ethernet-Adapter am Headset gefunden – Messung über den Router (IPv4).", "USB Ethernet adapter on the headset found – measuring via the router (IPv4).");
            else SetStatus("Nur WLAN gefunden – kein USB-NCM aktiv (Play For Dream: in den experimentellen Einstellungen einschalten) und kein USB-Ethernet-Adapter am Headset.",
                           "Only Wi-Fi found – no USB NCM active (Play For Dream: switch it on in the experimental settings) and no USB Ethernet adapter on the headset.");
        }
        int preferred = _targets.FindIndex(t => t.Kind == TargetKind.UsbNcm);
        if (preferred < 0) preferred = _targets.FindIndex(t => t.Kind == TargetKind.EthernetAdapter);
        _directTarget.SelectedIndex = Math.Max(0, preferred);
        _targetsSerial = _targets.Count > 0 ? device.Serial : null;
        _detecting = false;
        UpdateEnabled();
    }

    /// <summary>
    /// Quest: USB networking is only on while an app requests it. If usb0 has no address,
    /// ADBora requests it through its companion app (like Virtual Desktop) and keeps it on
    /// while the direct test is selected.
    /// </summary>
    private async Task EnsureQuestNcmAsync(AdbClient adb, string serial)
    {
        async Task<bool> HasUsbAddress() =>
            (await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "shell", "ip -o -4 addr show usb0")).Output.Contains("inet ");
        if (await HasUsbAddress()) { await WaitUsbLinkAsync(adb); return; }
        // A USB Ethernet adapter on the Quest (eth0) is handled by the same "EthernetOverUsb"
        // service: a TRANSPORT_USB request takes it over and restarts eth0 – which drops ADB
        // over that adapter. The headset's USB port is busy with the adapter anyway.
        if ((await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "shell", "ip -o -4 addr")).Output
            .Split('\n').Any(l => Regex.IsMatch(l, @"^\d+:\s+eth\d+\s+inet\s")))
            return;
        SetStatus("Schalte USB-Netzwerk (NCM) auf der Quest ein...", "Switching on USB networking (NCM) on the Quest...");
        try
        {
            if (_ncmHold is not null && _ncmHold.Serial != serial) await ReleaseNcmAsync();
            _ncmHold ??= new UsbNetworkHold(_state.AdbPath!, serial);
            await _ncmHold.StartAsync(t => Ui.AppendLine(_history, t));
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(500);
                if (await HasUsbAddress())
                {
                    await WaitUsbLinkAsync(adb);
                    return;
                }
            }
            await Dialogs.Message(Owner, Loc.T(
                "Die Quest hat das USB-Netzwerk nicht eingeschaltet. Beim ersten Mal fragt sie im Headset um Erlaubnis – bitte dort zustimmen (\"Immer erlauben\") und danach nochmals \"Erkennen\" klicken.",
                "The Quest did not switch on USB networking. The first time it asks for permission in the headset – please allow it there (\"Always allow\") and click \"Detect\" again."),
                "ADBora");
        }
        catch (Exception ex)
        {
            Ui.AppendLine(_history, Loc.T("USB-Netzwerk: ", "USB networking: ") + ex.Message);
        }
    }

    /// <summary>
    /// After switching on USB networking: wait (max. 10 s) until the headset has its IPv6 link-local address on usb0
    /// and the PC's USB network adapter is up – the IPv4 address comes first, IPv6 a moment later. Without DHCP on the
    /// PC side (e.g. Linux) the direct test runs over IPv6 link-local.
    /// </summary>
    private static async Task WaitUsbLinkAsync(AdbClient adb)
    {
        for (int i = 0; i < 20; i++)
        {
            bool headset = false;
            try { headset = (await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(5), "shell", "ip -6 -o addr show usb0")).Output.Contains("inet6 fe80:"); }
            catch { }
            bool pc = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up && IsUsbAdapter(n));
            if (headset && pc) return;
            await Task.Delay(500);
        }
    }

    private async Task ReleaseNcmAsync()
    {
        UsbNetworkHold? hold = _ncmHold;
        _ncmHold = null;
        if (hold is not null) await hold.StopAsync();
    }

    /// <summary>
    /// USB network adapter of the PC (the headset's USB NCM / RNDIS seen from the PC).
    /// Windows: by its description; Linux: the interface's device is a USB interface (/sys/class/net/X/device/uevent).
    /// </summary>
    private static bool IsUsbAdapter(NetworkInterface nic)
    {
        if (OperatingSystem.IsWindows())
            return Regex.IsMatch(nic.Description, "NCM|RNDIS|Remote NDIS|USB.*(Ethernet|Network)", RegexOptions.IgnoreCase);
        try
        {
            // USB network adapters (cdc_ncm, rndis_host, r8152 …) are USB interfaces; PCI cards / Wi-Fi are not.
            // (Not via the "device" symlink: it is relative and .NET resolves it against the wrong folder.)
            string uevent = $"/sys/class/net/{nic.Name}/device/uevent";
            return File.Exists(uevent) && File.ReadLines(uevent).Any(l => l.Trim() == "DEVTYPE=usb_interface");
        }
        catch { return Regex.IsMatch(nic.Name, @"^(usb\d|enx|enp\d+s\d+(f\d+)?u)"); }
    }

    private async Task<string> RunDirectAsync(DirectTarget target, int port, double duration, CancellationToken token)
    {
        _targetValue.Text = KindText(target.Kind); // full name (address, interface) in the history
        _attemptValue.Text = Loc.T("↓ Headset → PC", "↓ headset → PC");
        int streams = StreamCounts[Math.Max(0, _streams.SelectedIndex)];
        double down = await RunDirectAttemptAsync(target, port, duration, streams, download: true, token);
        _resultDownValue.Text = $"{down:0.0} Mbit/s";
        Ui.AppendLine(_history, $"{DateTime.Now:HH:mm:ss}    ↓ {Loc.T("Headset → PC", "headset → PC")}: {down:0.0} Mbit/s ({down / 8:0.0} MB/s)");
        _attemptValue.Text = Loc.T("↑ PC → Headset", "↑ PC → headset");
        double up = await RunDirectAttemptAsync(target, port, duration, streams, download: false, token);
        _resultUpValue.Text = $"{up:0.0} Mbit/s";
        Ui.AppendLine(_history, $"{DateTime.Now:HH:mm:ss}    ↑ {Loc.T("PC → Headset", "PC → headset")}: {up:0.0} Mbit/s ({up / 8:0.0} MB/s)");
        SetStatus($"Fertig – ↓ {down:0} Mbit/s · ↑ {up:0} Mbit/s", $"Finished – ↓ {down:0} Mbit/s · ↑ {up:0} Mbit/s");
        return $"{streams}× · ↓ {down:0.0} Mbit/s · ↑ {up:0.0} Mbit/s";
    }

    /// <summary>
    /// One direction with 1…n parallel connections: nc listens on the headset (one per port),
    /// the PC connects (no firewall rule needed on the PC). No data for 10 s = abort.
    /// </summary>
    private async Task<double> RunDirectAttemptAsync(DirectTarget target, int port, double duration, int streams, bool download, CancellationToken token)
    {
        CleanupConnection();
        await StopDeviceNcAsync(port);
        int[] ports = Enumerable.Range(port, streams).ToArray();
        SetStatus("Starte Gegenstelle auf dem Gerät...", "Starting counterpart on the device...");
        string one(int p) => download ? $"toybox nc -l -p {p} < /dev/zero" : $"toybox nc -l -p {p} > /dev/null";
        _ncProcess = _adb!.Start("shell", string.Join(" & ", ports.Select(one)) + (streams > 1 ? " & wait" : ""));

        SetStatus($"Verbinde mit {target.Address}...", $"Connecting to {target.Address}...");
        var clients = new List<TcpClient>();
        foreach (int p in ports)
        {
            TcpClient? client = null;
            for (int i = 0; i < 25 && client is null; i++)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token);
                var c = new TcpClient(target.Address.AddressFamily) { NoDelay = true };
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(2000);
                    await c.ConnectAsync(target.Address, p, cts.Token);
                    client = c;
                }
                catch when (!token.IsCancellationRequested) { c.Dispose(); }
            }
            if (client is null)
                throw new TimeoutException(Loc.T($"Keine Verbindung zu {target.Address} (Port {p}).", $"No connection to {target.Address} (port {p})."));
            client.Client.SendBufferSize = client.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            client.Client.SendTimeout = client.Client.ReceiveTimeout = StallTimeoutMs;
            clients.Add(client);
            lock (_directClients) _directClients.Add(client);
        }

        SetStatus(download ? "Messe Headset → PC..." : "Messe PC → Headset...", download ? "Measuring headset → PC..." : "Measuring PC → headset...");
        _phase = _chart.AddPhase(download ? Loc.T("↓ Headset → PC", "↓ headset → PC") : Loc.T("↑ PC → Headset", "↑ PC → headset"));
        _progress.Value = 0;
        Interlocked.Exchange(ref _liveSent, 0);
        Volatile.Write(ref _liveElapsed, 0);
        _liveDuration = duration;
        _lastUiTime = 0;
        _lastUiBytes = 0;
        _liveRunning = true;
        _uiTimer.Start();
        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            Task<long>[] loops = clients.Select(c => Task.Run(() => StreamLoop(c.Client, duration, download, sw, token), token)).ToArray();
            long[] totals = await Task.WhenAll(loops);
            double elapsed = Math.Max(sw.Elapsed.TotalSeconds, 0.000001);
            long sum = totals.Sum();
            double average = sum * 8.0 / elapsed / 1_000_000.0;
            ForceStats(elapsed, duration, sum, average);
            return average;
        }
        finally
        {
            _liveRunning = false;
            _uiTimer.Stop();
            CleanupConnection();
            await StopDeviceNcAsync(port);
        }
    }

    /// <summary>Worker thread: sends or receives for the test duration and adds to the live counter.</summary>
    private long StreamLoop(Socket socket, double duration, bool receive, Stopwatch sw, CancellationToken token)
    {
        byte[] buffer = new byte[ChunkSize];
        if (!receive) Array.Fill(buffer, (byte)'X');
        long total = 0;
        while (sw.Elapsed.TotalSeconds < duration)
        {
            token.ThrowIfCancellationRequested();
            int n;
            try
            {
                n = receive ? socket.Receive(buffer, 0, buffer.Length, SocketFlags.None)
                            : socket.Send(buffer, 0, buffer.Length, SocketFlags.None);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                throw new ConnectionLostException(Loc.T($"Seit {StallTimeoutMs / 1000} s keine Daten mehr – ist das Headset in den Standby gegangen?",
                                                        $"No data for {StallTimeoutMs / 1000} s – did the headset go to standby?"));
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                token.ThrowIfCancellationRequested();
                throw new ConnectionLostException(ex.Message);
            }
            if (n <= 0) throw new ConnectionLostException(Loc.T("Verbindung vom Gerät geschlossen.", "Connection closed by the device."));
            total += n;
            Interlocked.Add(ref _liveSent, n);
            double e = sw.Elapsed.TotalSeconds; // several threads: only move forward
            if (e > Volatile.Read(ref _liveElapsed)) Volatile.Write(ref _liveElapsed, e);
        }
        return total;
    }

    /// <summary>Quest: proximity sensor off during the test so the headset does not go to standby (restored afterwards).</summary>
    private async Task KeepAwakeAsync(bool on)
    {
        if (_adb is null) return;
        if (on && !_hmdModel.Contains("Quest", StringComparison.OrdinalIgnoreCase)) return;
        if (!on && !_proxApplied) return;
        try
        {
            await _adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(5), "shell",
                on ? "am broadcast -a com.oculus.vrpowermanager.prox_close" : "am broadcast -a com.oculus.vrpowermanager.automation_disable");
            _proxApplied = on;
        }
        catch { }
    }

    /// <summary>Ends a leftover netcat listener of this test on the device.</summary>
    private async Task StopDeviceNcAsync(int port)
    {
        if (_adb is null) return;
        try { await _adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(5), "shell", "pkill -f 'toybox nc -l -p'"); } catch { }
    }

    // ------------------------------------------------------------------
    // Wi-Fi state of the headset during the test (RSSI, link rates, retries)
    // ------------------------------------------------------------------

    private void StartWifiPolling(AdbClient adb)
    {
        _wifiCts?.Cancel();
        var cts = _wifiCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            long? lastRetried = null, lastSuccess = null, lastLost = null;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    AdbResult r = await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(5), "shell", "cmd wifi status");
                    string o = r.Output;
                    int Int(string pattern) => System.Text.RegularExpressions.Regex.Match(o, pattern) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int v) ? v : 0;
                    long Long(string pattern) => System.Text.RegularExpressions.Regex.Match(o, pattern, System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } m && long.TryParse(m.Groups[1].Value, out long v) ? v : -1;
                    int rssi = Int(@"RSSI: (-?\d+)");
                    if (rssi != 0)
                    {
                        long retried = Long(@"^retriedTxPackets: (\d+)"), success = Long(@"^successfulTxPackets: (\d+)"), lost = Long(@"^lostTxPackets: (\d+)");
                        double retry = 0;
                        long lostDelta = 0;
                        if (lastRetried is long lr && lastSuccess is long ls && retried >= lr && success >= ls)
                        {
                            long dr = retried - lr, ds = success - ls;
                            retry = dr + ds > 0 ? dr * 100.0 / (dr + ds) : 0;
                        }
                        if (lastLost is long ll && lost >= ll) lostDelta = lost - ll;
                        lastRetried = retried; lastSuccess = success; lastLost = lost;
                        _chart.Add(new WifiSample(_testClock.Elapsed.TotalSeconds, rssi, Int(@", Tx Link speed: (\d+)"), Int(@", Rx Link speed: (\d+)"), Int(@"Frequency: (\d+)"), retry, lostDelta));
                    }
                }
                catch { }
                try { await Task.Delay(2000, cts.Token); } catch { break; }
            }
        });
    }

    /// <summary>PNG report + CSV raw data (e.g. as evidence for the manufacturer's support).</summary>
    private async Task ExportReport()
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ADBora", "Speedtest");
            string tag = Backup.BackupService.DeviceTag(_hmdModel);
            string name = $"Speedtest_{tag}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            var header = new List<string>
            {
                $"ADBora Speedtest – {DisplayModel(_hmdModel)}",
                $"{Loc.T("Firmware", "Firmware")}: {(_firmware.Length > 0 ? _firmware : "?")} · {DateTime.Now:yyyy-MM-dd HH:mm}",
                _testDescription,
            };
            if (_chart.Analyze() is { } a) header.Add(a.Summary(!Loc.IsGerman));
            var (png, csv) = _chart.Export(folder, name, header);
            Ui.AppendLine(_history, Loc.T($"Bericht gespeichert: {png} / {Path.GetFileName(csv)}", $"Report saved: {png} / {Path.GetFileName(csv)}"));
            Platform.OpenFolder(folder);
        }
        catch (Exception ex)
        {
            await Dialogs.Message(Owner, ex.Message, "ADBora", MessageKind.Warning);
        }
    }

    /// <summary>Firmware for reports: on Meta Quest the Horizon OS version (ro.vros.build.version 209 → "v2.9",
    /// before v200 e.g. 78 → "v78") plus build id, otherwise ro.build.display.id.</summary>
    private async Task<string> GetFirmwareAsync()
    {
        try
        {
            string[] p = (await _adb!.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(5), "shell",
                "getprop ro.build.display.id; getprop ro.vros.build.version")).Output.Replace("\r", "").Split('\n');
            string build = p.Length > 0 ? p[0].Trim() : "";
            if (p.Length > 1 && int.TryParse(p[1].Trim(), out int v) && v > 0)
                return $"Horizon OS {(v >= 200 ? $"v{v / 100}.{v % 100}" : $"v{v}")} ({build})";
            return build;
        }
        catch { return ""; }
    }

    /// <summary>Readable headset name for reports, e.g. "PFDM MR" → "Play For Dream MR (PFDMR)", "Quest_3" → "Quest 3".</summary>
    private static string DisplayModel(string model)
    {
        string m = (model ?? "").Trim();
        if (m.Length == 0) return "?";
        if (m.Contains("PFDM", StringComparison.OrdinalIgnoreCase)) return "Play For Dream MR (PFDMR)";
        m = m.Replace('_', ' ');
        if (m.StartsWith("Quest", StringComparison.OrdinalIgnoreCase)) return "Meta " + m;
        return m;
    }

    private async Task RemoveReverseAsync(int port)
    {
        if (_adb is null || port <= 0) return;
        try { await _adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(5), "reverse", "--remove", $"tcp:{port}"); } catch { }
    }

    public void CancelOperation()
    {
        if (_cts is null) return;
        _cts.Cancel();
        SetStatus("Breche Test ab...", "Cancelling test...");
        CleanupConnection();
        UpdateEnabled();
    }

    private void CleanupConnection()
    {
        lock (_directClients)
        {
            foreach (TcpClient c in _directClients) try { c.Close(); } catch { }
            _directClients.Clear();
        }
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        _client = null;
        _listener = null;
        AdbClient.KillQuietly(_ncProcess);
        _ncProcess?.Dispose();
        _ncProcess = null;
    }

    private void ResetStats(double duration)
    {
        _attemptValue.Text = "-";
        _targetValue.Text = "-";
        _elapsedValue.Text = "0.0 s";
        _remainingValue.Text = $"{duration:0.0} s";
        _sentValue.Text = "0.00 GiB";
        _currentValue.Text = "0.0 Mbit/s";
        _averageValue.Text = "0.0 Mbit/s";
        _lastDisconnectValue.Text = "-";
        _resultDownValue.Text = "-";
        _resultUpValue.Text = "-";
        _progress.Value = 0;
        SetStatus("Bereit", "Ready");
        RenderHmd();
    }
}
