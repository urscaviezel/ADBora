using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
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
internal sealed class SpeedTestPage : UserControl, IPage
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
    private readonly RadioButton _pathDirect = Ui.Radio("TCP/IP direkt (USB-NCM oder WLAN)", "TCP/IP direct (USB NCM or Wi-Fi)");
    private readonly DarkComboBox _directTarget = Ui.Combo(330);
    private readonly DarkButton _detect = Ui.Button("Erkennen", "Detect");
    private readonly List<DirectTarget> _targets = new();
    private static readonly int[] StreamCounts = { 1, 4, 8 };
    private readonly DarkComboBox _streams = Ui.Combo(80);
    private readonly LiveValue _resultDownValue = new();
    private readonly LiveValue _resultUpValue = new();
    private readonly List<TcpClient> _directClients = new();
    private bool _proxApplied;
    private UsbNetworkHold? _ncmHold;
    private readonly SpeedChart _chart = new() { Dock = DockStyle.Fill };
    private readonly LiveValue _analysis = new() { ForeColor = Theme.Muted };
    private readonly DarkButton _export = Ui.Button("Bericht speichern (PNG + CSV)", "Save report (PNG + CSV)");
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
    private sealed record DirectTarget(string Name, IPAddress Address, bool Wifi = false);
    private readonly TextBox _duration = Ui.TextBox(110);

    private readonly DarkButton _check = Ui.Button("Gerät prüfen", "Check device");
    private readonly DarkButton _start = Ui.Button("Start", "Start", ButtonKind.Primary);
    private readonly DarkButton _cancel = Ui.Button("Abbrechen", "Cancel", ButtonKind.Danger);

    private readonly Label _hmdValue = Ui.Value();
    private readonly Label _statusValue = Ui.Value();
    private readonly LiveValue _attemptValue = new();
    private readonly LiveValue _targetValue = new();
    private readonly LiveValue _elapsedValue = new();
    private readonly LiveValue _remainingValue = new();
    private readonly LiveValue _sentValue = new();
    private readonly LiveValue _currentValue = new();
    private readonly LiveValue _averageValue = new();
    private readonly Label _lastDisconnectValue = Ui.Value();
    private readonly DarkProgressBar _progress = new() { Maximum = 1000, Height = 26 };
    private readonly TextBox _history = Ui.Console();

    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 250 };

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

    public SpeedTestPage(AppState state)
    {
        _state = state;
        BackColor = Theme.Background;
        DoubleBuffered = true;
        Padding = new Padding(20, 16, 20, 16);

        BuildUi();

        _port.Text = _state.Settings.SpeedPort.ToString(CultureInfo.InvariantCulture);
        _rate.Text = _state.Settings.SpeedRateMbps.ToString("0.###", CultureInfo.InvariantCulture);
        _duration.Text = _state.Settings.SpeedDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        _max.Checked = true; // always start in max-speed mode (as before)
        UpdateModeState();
        ResetStats(ParseDouble(_duration.Text, 600));

        _uiTimer.Tick += (_, _) => PublishLiveStats();
        _state.BusyChanged += UpdateEnabled;
        HandleDestroyed += (_, _) => _ncmHold?.Dispose();
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
        _state.DevicesChanged += () => { if (IsHandleCreated) BeginInvoke(new Action(AutoDetect)); };
        UpdateEnabled();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Settings card -----------------------------------------------------
        var settings = new Card("Verbindung / Testeinstellungen", "Connection / Test settings") { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(0, 0, 10, 12) };
        var grid = Ui.Grid(2);
        Ui.AddRow(grid, Ui.Label("TCP-Port:", "TCP port:", muted: true), Ui.Row(_port));

        var paths = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, BackColor = Color.Transparent, WrapContents = false, Margin = new Padding(0, 6, 0, 2) };
        paths.Controls.Add(_pathAdb);
        paths.Controls.Add(_pathDirect);
        _pathAdb.Checked = !_state.Settings.SpeedDirect;
        _pathDirect.Checked = _state.Settings.SpeedDirect;
        _pathDirect.CheckedChanged += async (_, _) =>
        {
            _state.Settings.SpeedDirect = _pathDirect.Checked;
            _state.Settings.Save();
            if (!_pathDirect.Checked) await ReleaseNcmAsync();
            UpdateEnabled();
            AutoDetect();
        };
        Ui.AddRow(grid, Ui.Label("Messweg:", "Test path:", muted: true), paths);
        _detect.Click += async (_, _) => await DetectTargetsAsync();
        _directTarget.Items.Add(Loc.T("– noch nicht erkannt –", "– not detected yet –"));
        _directTarget.SelectedIndex = 0;
        var targetRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _directTarget.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _directTarget.MinimumSize = new Size(Theme.S(120), 0);
        targetRow.Controls.Add(_directTarget, 0, 0);
        targetRow.Controls.Add(_detect, 1, 0);
        Ui.AddRow(grid, Ui.Label("Ziel:", "Target:", muted: true), targetRow);
        foreach (int n in StreamCounts) _streams.Items.Add(n.ToString(CultureInfo.InvariantCulture));
        _streams.SelectedIndex = Math.Max(0, Array.IndexOf(StreamCounts, _state.Settings.SpeedStreams));
        _streams.SelectedIndexChanged += (_, _) => { _state.Settings.SpeedStreams = StreamCounts[Math.Max(0, _streams.SelectedIndex)]; _state.Settings.Save(); };
        var streamsHint = Ui.Label("parallele TCP-Verbindungen (mehrere = vergleichbar mit Online-Speedtests)", "parallel TCP connections (several = comparable to online speed tests)", muted: true);
        streamsHint.Margin = new Padding(0, 8, 0, 0);
        var streamsRow = Ui.Row(_streams, streamsHint);
        streamsRow.WrapContents = true;
        Ui.WrapTo(streamsHint, settings, 260);
        Ui.AddRow(grid, Ui.Label("Verbindungen:", "Connections:", muted: true), streamsRow);

        var modes = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, BackColor = Color.Transparent, WrapContents = false, Margin = new Padding(0, 6, 0, 6) };
        modes.Controls.Add(_max);
        modes.Controls.Add(_limited);
        _limited.CheckedChanged += (_, _) => { UpdateModeState(); SaveSettings(); };
        _max.CheckedChanged += (_, _) => { UpdateModeState(); SaveSettings(); };
        Ui.AddRow(grid, Ui.Label("Modus:", "Mode:", muted: true), modes);

        var mbit = Ui.Value("Mbit/s");
        mbit.Margin = new Padding(0, 8, 0, 0);
        Ui.AddRow(grid, Ui.Label("Zielrate:", "Target rate:", muted: true), Ui.Row(_rate, mbit));
        var seconds = Ui.Label("Sekunden pro Versuch", "seconds per attempt", muted: true);
        seconds.Margin = new Padding(0, 8, 0, 0);
        Ui.AddRow(grid, Ui.Label("Testdauer:", "Test duration:", muted: true), Ui.Row(_duration, seconds));
        foreach (TextBox box in new[] { _port, _rate, _duration })
            box.Leave += (_, _) => SaveSettings();

        _check.Click += async (_, _) => await CheckHmdAsync();
        _start.Click += async (_, _) => await StartTestAsync();
        _cancel.Click += (_, _) => CancelOperation();
        var buttons = Ui.Row(_check, _start, _cancel);
        buttons.Margin = new Padding(0, 12, 0, 8);
        Ui.AddRow(grid, buttons);
        grid.SetColumnSpan(buttons, 2);

        var info = Ui.Label(
            "Max-Speed-Modus: Der erste Versuch läuft ohne Limit. Wird die Verbindung getrennt, baut das Tool ADB/Netcat automatisch neu auf und reduziert die Datenrate schrittweise, bis ein kompletter Test stabil durchläuft.",
            "Max-Speed mode: The first attempt runs without a bandwidth limit. If the connection is lost, the tool automatically rebuilds ADB/Netcat and gradually reduces the transfer rate until a complete test runs stably.",
            muted: true);
        Ui.WrapTo(info, settings);
        info.Margin = new Padding(0, 6, 0, 0);
        Ui.AddRow(grid, info);
        grid.SetColumnSpan(info, 2);

        var how = Ui.Label(
            "Messpfad ADB: PC-TCP-Sender → adb reverse → USB/WLAN → toybox nc → /dev/null. Der Wert ist der Durchsatz dieses ADB/TCP-Pfads, nicht die theoretische USB-Busgeschwindigkeit. " +
            "TCP/IP direkt: PC ↔ Netzwerkadapter ↔ toybox nc auf dem Headset, ohne ADB – über USB-NCM (USB-Netzwerk; bei der Quest schaltet ADBora es bei Bedarf über die Begleit-App ein, bei der Play For Dream in den experimentellen Einstellungen aktivieren) oder WLAN; gemessen wird in beide Richtungen. Es wird nichts auf dem Gerät gespeichert.",
            "ADB path: PC TCP sender → adb reverse → USB/WLAN → toybox nc → /dev/null. The value is the throughput of this ADB/TCP path, not the theoretical USB bus speed. " +
            "TCP/IP direct: PC ↔ network adapter ↔ toybox nc on the headset, without ADB – over USB NCM (USB networking; on the Quest ADBora switches it on via the companion app when needed, on the Play For Dream enable it in the experimental settings) or Wi-Fi; both directions are measured. Nothing is written to the device.", muted: true);
        Ui.WrapTo(how, settings);
        how.Margin = new Padding(0, 10, 0, 0);
        Ui.AddRow(grid, how);
        grid.SetColumnSpan(how, 2);

        settings.SetContent(grid);
        root.Controls.Add(settings, 0, 0);

        // Status card ---------------------------------------------------------
        var status = new Card("Status", "Status") { Composited = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(10, 0, 0, 12) };
        var statusGrid = Ui.Grid(2);
        statusGrid.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, 150);
        void Row(string de, string en, Control value)
        {
            if (value is Label label)
            {
                // Rarely changing texts (device, status) may wrap
                label.AutoEllipsis = false;
                label.MinimumSize = new Size(160, 0);
                Ui.WrapTo(label, status, 170);
            }
            Ui.AddRow(statusGrid, Ui.Label(de, en, muted: true), value);
        }
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
        // Live values (LiveValue): fixed size, draw only themselves → no flicker
        _currentValue.Font = _averageValue.Font = _resultDownValue.Font = _resultUpValue.Font = Theme.SectionFont;
        _averageValue.ForeColor = Theme.Accent;
        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(0, 12, 0, 4);
        Ui.AddRow(statusGrid, _progress);
        statusGrid.SetColumnSpan(_progress, 2);
        status.SetContent(statusGrid);
        root.Controls.Add(status, 1, 0);

        // History ------------------------------------------------------------
        var historyCard = new Card("Protokoll (diese Sitzung)", "Log (this session)") { Dock = DockStyle.Fill, Margin = new Padding(0) };
        historyCard.SetContent(_history, fill: true);

        // Throughput chart -----------------------------------------------------
        var chartCard = new Card("Durchsatz-Verlauf", "Throughput history") { Dock = DockStyle.Top, Height = Theme.S(340) };
        var chartBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Color.Transparent };
        chartBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        chartBox.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        chartBox.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        chartBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _analysis.Text = Loc.T("Einbrüche (unter 50 % des Medians) gelb, Aussetzer rot markiert.", "Dips (below 50 % of the median) marked yellow, stalls red.");
        _analysis.Margin = new Padding(0, 6, 8, 6);
        _export.Enabled = false;
        _export.Click += (_, _) => ExportReport();
        chartBox.Controls.Add(_analysis, 0, 0);
        chartBox.Controls.Add(_export, 1, 0);
        chartBox.Controls.Add(_chart, 0, 1);
        chartBox.SetColumnSpan(_chart, 2);
        chartCard.SetContent(chartBox, fill: true);
        var chartSpacer = new Panel { Dock = DockStyle.Top, Height = Theme.S(12), BackColor = Color.Transparent };

        Controls.Add(historyCard);
        Controls.Add(chartSpacer);
        Controls.Add(chartCard);
        Controls.Add(root);

        // Narrow window: stack settings above status; the page scrolls and the
        // history gets a fixed height below.
        bool narrowMode = false;
        void UpdateScroll()
        {
            AutoScrollMinSize = narrowMode ? new Size(0, root.Height + chartCard.Height + chartSpacer.Height + historyCard.Height + Padding.Vertical) : Size.Empty;
        }
        Ui.Responsive(this, root, settings, status, breakpoint: 940, firstPercent: 46, changed: narrow =>
        {
            narrowMode = narrow;
            AutoScroll = narrow;
            historyCard.Dock = narrow ? DockStyle.Top : DockStyle.Fill;
            if (narrow) historyCard.Height = Theme.S(220);
            settings.Margin = narrow ? new Padding(0, 0, 0, Theme.S(12)) : new Padding(0, 0, Theme.S(10), Theme.S(12));
            status.Margin = narrow ? new Padding(0, 0, 0, Theme.S(12)) : new Padding(Theme.S(10), 0, 0, Theme.S(12));
            UpdateScroll();
        });
        root.SizeChanged += (_, _) => UpdateScroll();
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
        if (!_pathDirect.Checked || _cts is not null || _detecting || !Visible) return;
        if (_state.SelectedDevice is not { IsReady: true } d || !_state.HasAdb || _state.IsBusy) return;
        if (_targets.Count > 0 && _targetsSerial == d.Serial) return;
        _ = DetectTargetsAsync();
    }

    private void ClearTargets()
    {
        _targets.Clear();
        _targetsSerial = null;
        _directTarget.Items.Clear();
        _directTarget.Items.Add(Loc.T("– noch nicht erkannt –", "– not detected yet –"));
        _directTarget.SelectedIndex = 0;
    }

    private void UpdateModeState() => _rate.Enabled = !_max.Checked;

    private void UpdateEnabled()
    {
        bool running = _cts is not null;
        bool idle = !_state.IsBusy;
        bool ready = _state.HasAdb && _state.SelectedDevice?.IsReady == true;
        _check.Enabled = idle && ready;
        _start.Enabled = idle && ready;
        _cancel.Enabled = running && !_cts!.IsCancellationRequested;
        bool direct = _pathDirect.Checked;
        _port.Enabled = _duration.Enabled = _pathAdb.Enabled = _pathDirect.Enabled = !running;
        _max.Enabled = _limited.Enabled = !running && !direct;
        _rate.Enabled = !running && !_max.Checked && !direct;
        _directTarget.Enabled = _streams.Enabled = !running && direct;
        _detect.Enabled = idle && ready && direct && !_detecting;
        if (direct) _start.Enabled = idle && ready && SelectedTarget is not null;
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
        _hmdValue.ForeColor = _hmdChecked ? Theme.Accent : Theme.Text;
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

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;

    private bool ValidateInputs(out int port, out double duration, out double rate)
    {
        port = 0; duration = 0; rate = 0;

        if (!_state.HasAdb)
        {
            MessageBox.Show(FindForm(), Loc.T("ADB ist nicht eingerichtet. Bitte im Tab „Allgemeine Einstellungen“ adb.exe auswählen.",
                "ADB is not set up. Please choose adb.exe in the \"General settings\" tab."), "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        if (_state.SelectedDevice is not { IsReady: true })
        {
            MessageBox.Show(FindForm(), Loc.T("Bitte ein verbundenes, autorisiertes Gerät auswählen.", "Please select a connected, authorized device."), "ADBora");
            return false;
        }
        if (!int.TryParse(_port.Text, out port) || port is < 1 or > 65535)
        {
            MessageBox.Show(FindForm(), Loc.T("Ungültiger TCP-Port.", "Invalid TCP port."), "ADBora");
            return false;
        }
        duration = ParseDouble(_duration.Text, -1);
        if (duration <= 0)
        {
            MessageBox.Show(FindForm(), Loc.T("Ungültige Testdauer.", "Invalid test duration."), "ADBora");
            return false;
        }
        if (_limited.Checked)
        {
            rate = ParseDouble(_rate.Text, -1);
            if (rate <= 0)
            {
                MessageBox.Show(FindForm(), Loc.T("Ungültige Zielrate.", "Invalid target rate."), "ADBora");
                return false;
            }
        }

        SaveSettings();
        return true;
    }

    // ------------------------------------------------------------------

    private async Task CheckHmdAsync()
    {
        if (!ValidateInputs(out _, out _, out _))
            return;
        if (!_state.TryBeginOperation(Loc.T("Gerät wird geprüft", "Checking device")))
            return;

        _adb = _state.DeviceAdb;
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
            _hmdValue.ForeColor = Theme.Error;
        }
        finally
        {
            _state.EndOperation();
        }
    }

    private async Task StartTestAsync()
    {
        if (_cts is not null || !ValidateInputs(out int port, out double duration, out double rate))
            return;
        if (!_state.TryBeginOperation(Loc.T("Speedtest läuft", "Speed test running")))
            return;

        _adb = _state.DeviceAdb;
        _cts = new CancellationTokenSource();
        Ui.Frozen(this, () => { UpdateEnabled(); ResetStats(duration); }); // one repaint instead of many

        bool max = _max.Checked;
        bool direct = _pathDirect.Checked;
        string result = "";

        try
        {
            _hmdModel = await GetModelAsync(_cts.Token);
            _hmdChecked = true;
            RenderHmd();
            await KeepAwakeAsync(true);
            _firmware = (await _adb!.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(5), "shell", "getprop ro.build.display.id")).Output.Trim();
            _chart.Clear();
            _export.Enabled = false;
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
            MessageBox.Show(FindForm(), ex.Message, "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            _export.Enabled = _chart.HasData;
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
            Ui.AppendLine(_history, $"{DateTime.Now:HH:mm:ss}  {_hmdModel} ({_state.SelectedDevice?.Serial})  ·  {mode}  ·  {duration:0} s  →  {result}");
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
    private static void SetText(Control label, string text)
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
        _detect.Enabled = false;
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

            foreach (string usbIf in v4.Select(x => x.If).Concat(v6.Select(x => x.If)).Where(IsUsb).Distinct())
            {
                // IPv4 if the PC is in the same subnet
                var ip4 = v4.FirstOrDefault(x => x.If == usbIf);
                if (ip4.Ip is not null && pcNics.Any(n => n.GetIPProperties().UnicastAddresses.Any(u => SameSubnet(u.Address, ip4.Ip, ip4.Prefix))))
                {
                    found.Add(new DirectTarget($"USB-NCM ({usbIf}) · {ip4.Ip}", ip4.Ip));
                    continue;
                }
                // otherwise IPv6 link-local over every USB network adapter of the PC
                var ip6 = v6.FirstOrDefault(x => x.If == usbIf);
                if (ip6.Ip is null) continue;
                foreach (NetworkInterface nic in pcNics.Where(n => Regex.IsMatch(n.Description, "NCM|RNDIS|Remote NDIS|USB.*(Ethernet|Network)", RegexOptions.IgnoreCase)))
                {
                    if (!nic.Supports(NetworkInterfaceComponent.IPv6)) continue;
                    var addr = new IPAddress(ip6.Ip.GetAddressBytes(), nic.GetIPProperties().GetIPv6Properties().Index);
                    found.Add(new DirectTarget($"USB-NCM ({usbIf}) · IPv6 · {nic.Name}", addr));
                }
            }
            foreach (var w in v4.Where(x => x.If.StartsWith("wlan", StringComparison.Ordinal)))
                found.Add(new DirectTarget($"{Loc.T("WLAN", "Wi-Fi")} ({w.If}) · {w.Ip}", w.Ip, Wifi: true));
        }
        catch (Exception ex)
        {
            SetStatus($"Fehler: {ex.Message}", $"Error: {ex.Message}");
        }

        _targets.Clear();
        _targets.AddRange(found);
        _directTarget.Items.Clear();
        foreach (DirectTarget t in _targets) _directTarget.Items.Add(t.Name);
        if (_targets.Count == 0)
        {
            _directTarget.Items.Add(Loc.T("– keine Schnittstelle gefunden –", "– no interface found –"));
            SetStatus("Keine Netzwerk-Schnittstelle gefunden.", "No network interface found.");
        }
        else
        {
            int usb = _targets.FindIndex(t => t.Name.StartsWith("USB", StringComparison.Ordinal));
            SetStatus(usb >= 0 ? "USB-NCM gefunden." : "Nur WLAN gefunden – USB-NCM ist nicht aktiv (Play For Dream: in den experimentellen Einstellungen einschalten).",
                      usb >= 0 ? "USB NCM found." : "Only Wi-Fi found – USB NCM is not active (Play For Dream: switch it on in the experimental settings).");
        }
        _directTarget.SelectedIndex = Math.Max(0, _targets.FindIndex(t => t.Name.StartsWith("USB", StringComparison.Ordinal)));
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
        if (await HasUsbAddress()) return;
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
                    await Task.Delay(1500); // let Windows bring up its USB network adapter
                    return;
                }
            }
            MessageBox.Show(FindForm(), Loc.T(
                "Die Quest hat das USB-Netzwerk nicht eingeschaltet. Beim ersten Mal fragt sie im Headset um Erlaubnis – bitte dort zustimmen (\"Immer erlauben\") und danach nochmals \"Erkennen\" klicken.",
                "The Quest did not switch on USB networking. The first time it asks for permission in the headset – please allow it there (\"Always allow\") and click \"Detect\" again."),
                "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Ui.AppendLine(_history, Loc.T("USB-Netzwerk: ", "USB networking: ") + ex.Message);
        }
    }

    private async Task ReleaseNcmAsync()
    {
        UsbNetworkHold? hold = _ncmHold;
        _ncmHold = null;
        if (hold is not null) await hold.StopAsync();
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, int prefix)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork || a.Equals(b)) return false;
        uint ua = BitConverter.ToUInt32(a.GetAddressBytes().Reverse().ToArray()), ub = BitConverter.ToUInt32(b.GetAddressBytes().Reverse().ToArray());
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return (ua & mask) == (ub & mask);
    }

    private async Task<string> RunDirectAsync(DirectTarget target, int port, double duration, CancellationToken token)
    {
        _targetValue.Text = target.Name.Split(" · ")[0]; // e.g. "USB-NCM (usb0)"; full name in the history
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
    private void ExportReport()
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ADBora", "Speedtest");
            string tag = Backup.BackupService.DeviceTag(_hmdModel);
            string name = $"Speedtest_{tag}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            var header = new List<string>
            {
                $"ADBora Speedtest – {_hmdModel}",
                $"{Loc.T("Firmware", "Firmware")}: {_firmware} · {Loc.T("Gerät", "Device")}: {_state.SelectedDevice?.Serial} · {DateTime.Now:yyyy-MM-dd HH:mm}",
                _testDescription,
            };
            if (_chart.Analyze() is { } a) header.Add(a.Summary(!Loc.IsGerman));
            var (png, csv) = _chart.Export(folder, name, header);
            Ui.AppendLine(_history, Loc.T($"Bericht gespeichert: {png} / {Path.GetFileName(csv)}", $"Report saved: {png} / {Path.GetFileName(csv)}"));
            SettingsPage.OpenPath(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(), ex.Message, "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
