using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
    private readonly TextBox _duration = Ui.TextBox(110);

    private readonly DarkButton _check = Ui.Button("Gerät prüfen", "Check device");
    private readonly DarkButton _start = Ui.Button("Start", "Start", ButtonKind.Primary);
    private readonly DarkButton _cancel = Ui.Button("Abbrechen", "Cancel", ButtonKind.Danger);

    private readonly Label _hmdValue = Ui.Value();
    private readonly Label _statusValue = Ui.Value();
    private readonly Label _attemptValue = Ui.Value();
    private readonly Label _targetValue = Ui.Value();
    private readonly Label _elapsedValue = Ui.Value();
    private readonly Label _remainingValue = Ui.Value();
    private readonly Label _sentValue = Ui.Value();
    private readonly Label _currentValue = Ui.Value();
    private readonly Label _averageValue = Ui.Value();
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
        _state.SelectedDeviceChanged += () =>
        {
            if (_cts is null) { _hmdChecked = false; RenderHmd(); }
            UpdateEnabled();
        };
        Loc.LanguageChanged += () => { RenderHmd(); _statusValue.Text = Loc.T(_status.De, _status.En); };
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
            "Messpfad: PC-TCP-Sender → adb reverse → USB/WLAN → toybox nc → /dev/null. Es wird nichts auf dem Gerät gespeichert. " +
            "Der Wert ist der Durchsatz dieses ADB/TCP-Pfads, nicht die theoretische USB-Busgeschwindigkeit.",
            "Test path: PC TCP sender → adb reverse → USB/WLAN → toybox nc → /dev/null. Nothing is written to the device. " +
            "The value is the throughput of this ADB/TCP path, not the theoretical USB bus speed.", muted: true);
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
        void Row(string de, string en, Label value)
        {
            value.AutoEllipsis = false;
            // A minimum width keeps the table from re-laying out on every value change.
            value.MinimumSize = new Size(160, 0);
            Ui.WrapTo(value, status, 170);
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
        _currentValue.Font = _averageValue.Font = Theme.SectionFont;
        _averageValue.ForeColor = Theme.Accent;
        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(0, 12, 0, 4);
        Ui.AddRow(statusGrid, _progress);
        statusGrid.SetColumnSpan(_progress, 2);
        status.SetContent(statusGrid);
        root.Controls.Add(status, 1, 0);

        // History ------------------------------------------------------------
        var historyCard = new Card("Verlauf (diese Sitzung)", "History (this session)") { Dock = DockStyle.Fill, Margin = new Padding(0) };
        historyCard.SetContent(_history, fill: true);
        Controls.Add(historyCard);
        Controls.Add(root);

        // Narrow window: stack settings above status; the page scrolls and the
        // history gets a fixed height below.
        bool narrowMode = false;
        void UpdateScroll()
        {
            AutoScrollMinSize = narrowMode ? new Size(0, root.Height + historyCard.Height + Padding.Vertical) : Size.Empty;
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

    public void OnActivated() => UpdateEnabled();

    private void UpdateModeState() => _rate.Enabled = !_max.Checked;

    private void UpdateEnabled()
    {
        bool running = _cts is not null;
        bool idle = !_state.IsBusy;
        bool ready = _state.HasAdb && _state.SelectedDevice?.IsReady == true;
        _check.Enabled = idle && ready;
        _start.Enabled = idle && ready;
        _cancel.Enabled = running && !_cts!.IsCancellationRequested;
        _port.Enabled = _duration.Enabled = _max.Enabled = _limited.Enabled = !running;
        _rate.Enabled = !running && !_max.Checked;
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
        UpdateEnabled();
        ResetStats(duration);
        bool max = _max.Checked;
        string result = "";

        try
        {
            _hmdModel = await GetModelAsync(_cts.Token);
            _hmdChecked = true;
            RenderHmd();

            result = max
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
            CleanupConnection();
            await RemoveReverseAsync(port);
            // Clean ADB state after a USB test, as in the stand-alone tool. Not for
            // Wi-Fi devices: stopping the server would drop the Wi-Fi connection.
            if (_adb is not null && !_adb.Serial.Contains(':'))
                await _adb.KillServerAsync();
            _cts.Dispose();
            _cts = null;
            _state.EndOperation();
            UpdateEnabled();

            string mode = max ? "Max" : $"{Loc.T("Limit", "Limit")} {rate:0} Mbit/s";
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
    private static void SetText(Label label, string text)
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
        _progress.Value = 0;
        SetStatus("Bereit", "Ready");
        RenderHmd();
    }
}
