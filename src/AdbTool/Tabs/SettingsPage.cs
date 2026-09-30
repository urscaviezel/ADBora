using System.Diagnostics;
using System.Runtime.InteropServices;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>Tab 1: language, ADB setup, installed versions and device information.</summary>
internal sealed class SettingsPage : UserControl, IPage
{
    private readonly AppState _state;

    private readonly RadioButton _german = new DarkRadio { Text = "Deutsch", ForeColor = Theme.Text, BackColor = Color.Transparent, Font = Theme.BaseFont, Margin = new Padding(0, 6, 20, 4) };
    private readonly RadioButton _english = new DarkRadio { Text = "English", ForeColor = Theme.Text, BackColor = Color.Transparent, Font = Theme.BaseFont, Margin = new Padding(0, 6, 20, 4) };
    private readonly CheckBox _stopServer = Ui.CheckBox("ADB-Server beim Beenden stoppen", "Stop ADB server on exit");

    private readonly TextBox _adbPath = Ui.TextBox(420);
    private readonly Label _adbStatus = Ui.Value();
    private readonly Label _adbVersion = Ui.Value();
    private readonly Label _adbBuild = Ui.Value();
    private readonly Label _toolVersion = Ui.Value();
    private readonly Label _dotnetVersion = Ui.Value();
    private readonly Label _osVersion = Ui.Value();
    private readonly Label _dataMode = Ui.Value();

    private readonly TableLayoutPanel _deviceList = Ui.Grid(4);
    private readonly TableLayoutPanel _deviceInfo = Ui.Grid(2);
    private readonly Label _deviceInfoStatus = Ui.Value("");
    private readonly TextBox _connectAddress = Ui.TextBox(260);
    private readonly DarkButton _refreshInfo = Ui.Button("Infos aktualisieren", "Refresh info");
    private readonly DarkButton _copyInfo = Ui.Button("Kopieren", "Copy");
    private readonly DarkButton _connect = Ui.Button("WLAN verbinden", "Connect Wi-Fi");
    private readonly DarkButton _disconnect = Ui.Button("Trennen", "Disconnect");
    private readonly Label _wifiStatus = Ui.Value("");

    private List<(string De, string En, string Value)> _info = new();
    private int _infoRequest;

    public SettingsPage(AppState state)
    {
        _state = state;
        BackColor = Theme.Background;
        DoubleBuffered = true;
        AutoScroll = true;
        Padding = new Padding(20, 16, 20, 16);

        BuildUi();

        _state.AdbChanged += UpdateAdbInfo;
        _state.DevicesChanged += UpdateDeviceList;
        _state.SelectedDeviceChanged += () => { UpdateDeviceList(); if (Visible) _ = LoadDeviceInfoAsync(); };
        _state.BusyChanged += UpdateEnabled;
        Loc.LanguageChanged += () => { UpdateAdbInfo(); UpdateDeviceList(); RenderInfo(); };

        UpdateAdbInfo();
        UpdateDeviceList();
        UpdateEnabled();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            BackColor = Color.Transparent
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        Controls.Add(root);

        var left = Column();
        var right = Column();
        left.Margin = new Padding(0, 0, 10, 0);
        right.Margin = new Padding(10, 0, 0, 0);
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(right, 1, 0);
        Ui.Responsive(this, root, left, right, breakpoint: 980, changed: narrow =>
        {
            left.Margin = narrow ? Padding.Empty : new Padding(0, 0, Theme.S(10), 0);
            right.Margin = narrow ? Padding.Empty : new Padding(Theme.S(10), 0, 0, 0);
        });

        // --- General ---------------------------------------------------
        var general = new Card("Allgemein", "General") { Composited = true };
        var generalGrid = Ui.Grid(2);
        _german.Checked = !Loc.IsEnglish;
        _english.Checked = Loc.IsEnglish;
        void LanguageChosen()
        {
            string lang = _english.Checked ? "en" : "de";
            if (lang == _state.Settings.Language) return;
            _state.Settings.Language = lang;
            _state.Settings.Save();
            Loc.SetLanguage(lang);
        }
        _german.CheckedChanged += (_, _) => { if (_german.Checked) LanguageChosen(); };
        _english.CheckedChanged += (_, _) => { if (_english.Checked) LanguageChosen(); };
        _stopServer.Checked = _state.Settings.StopAdbServerOnExit;
        _stopServer.CheckedChanged += (_, _) =>
        {
            _state.Settings.StopAdbServerOnExit = _stopServer.Checked;
            _state.Settings.Save();
        };
        Ui.AddRow(generalGrid, Ui.Label("Sprache / Language", "Language / Sprache", muted: true), Ui.Row(_german, _english));
        Ui.AddRow(generalGrid, new Label { Width = 1 }, _stopServer);
        Ui.AddRow(generalGrid, Ui.Label("ADBora", "ADBora", muted: true), _toolVersion);
        Ui.AddRow(generalGrid, Ui.Label(".NET-Laufzeit", ".NET runtime", muted: true), _dotnetVersion);
        Ui.AddRow(generalGrid, Ui.Label("Betriebssystem", "Operating system", muted: true), _osVersion);
        Ui.AddRow(generalGrid, Ui.Label("Datenablage", "Data storage", muted: true), _dataMode);
        void RenderDataMode() => _dataMode.Text = (AppSettings.IsPortable
            ? Loc.T("Portabel", "Portable")
            : Loc.T("Installiert", "Installed")) + " · " + AppSettings.DataDirectory;
        RenderDataMode();
        Loc.LanguageChanged += RenderDataMode;
        Ui.WrapTo(_dataMode, general, 150);
        var dataFolder = Ui.Button("Datenordner öffnen", "Open data folder");
        dataFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            OpenPath(AppSettings.DataDirectory);
        };
        Ui.AddRow(generalGrid, new Label { Width = 1 }, Ui.Row(dataFolder));
        general.SetContent(generalGrid);
        left.Controls.Add(general);

        _toolVersion.Text = AppInfo.Version;
        _dotnetVersion.Text = RuntimeInformation.FrameworkDescription;
        _osVersion.Text = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

        // --- ADB -------------------------------------------------------
        var adb = new Card("ADB (Android Debug Bridge)", "ADB (Android Debug Bridge)");
        var adbGrid = Ui.Grid(2);
        _adbPath.ReadOnly = true;
        _adbPath.Width = 380;
        _adbPath.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Ui.AddRow(adbGrid, Ui.Label("adb.exe", "adb.exe", muted: true), _adbPath);

        var choose = Ui.Button("adb.exe auswählen …", "Choose adb.exe …");
        var search = Ui.Button("Automatisch suchen", "Search automatically");
        var download = Ui.Button("Platform-Tools herunterladen", "Download Platform-Tools");
        var help = Ui.Button("Einrichtungshilfe", "Setup help");
        choose.Click += (_, _) => ChooseAdb();
        search.Click += (_, _) =>
        {
            _state.Settings.AdbPath = "";
            _state.RediscoverAdb();
            if (!_state.HasAdb)
                MessageBox.Show(this, Loc.T("adb.exe wurde nicht gefunden.", "adb.exe was not found."), "ADBora",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        download.Click += (_, _) => OpenUrl(AdbLocator.PlatformToolsUrl + (Loc.IsEnglish ? "?hl=en" : "?hl=de"));
        help.Click += (_, _) => ShowSetupHelp(missing: false);
        // Two fixed rows instead of a wrapping panel: a wrapping FlowLayoutPanel
        // inside an auto-sized table can clip its second line.
        var buttons = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 4) };
        buttons.Controls.Add(Ui.Row(choose, search), 0, 0);
        buttons.Controls.Add(Ui.Row(download, help), 0, 1);
        Ui.AddRow(adbGrid, new Label { Width = 1 }, buttons);
        Ui.AddRow(adbGrid, Ui.Label("Status", "Status", muted: true), _adbStatus);
        Ui.AddRow(adbGrid, Ui.Label("ADB-Version", "ADB version", muted: true), _adbVersion);
        Ui.AddRow(adbGrid, Ui.Label("Platform-Tools", "Platform-Tools", muted: true), _adbBuild);
        adb.SetContent(adbGrid);
        left.Controls.Add(adb);

        // --- Devices ---------------------------------------------------
        var devices = new Card("Angeschlossene Geräte", "Connected devices");
        var devicesBox = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Color.Transparent, Dock = DockStyle.Top };
        devicesBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        devicesBox.Controls.Add(_deviceList);
        var hint = Ui.Label("Das aktive Gerät wird oben rechts gewählt und gilt für alle Tabs.",
            "The active device is selected at the top right and applies to all tabs.", muted: true);
        hint.MaximumSize = new Size(420, 0);
        devicesBox.Controls.Add(hint);

        Ui.Placeholder(_connectAddress, "IP[:Port] – leer = IP des USB-Geräts", "IP[:port] – empty = IP of the USB device");
        _connectAddress.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            if (_connect.Enabled) await ConnectAsync(true);
        };
        _connect.Click += async (_, _) => await ConnectAsync(true);
        _disconnect.Click += async (_, _) => await ConnectAsync(false);
        devicesBox.Controls.Add(Ui.Row(_connectAddress, _connect, _disconnect));
        _wifiStatus.ForeColor = Theme.Muted;
        _wifiStatus.Margin = new Padding(0, 2, 0, 4);
        Ui.WrapTo(_wifiStatus, devices);
        devicesBox.Controls.Add(_wifiStatus);
        var wifiHint = Ui.Label(
            "WLAN-ADB: Das Headset muss einmal per USB angeschlossen sein. Beim Verbinden schaltet das Tool ADB automatisch auf WLAN um (adb tcpip 5555) " +
            "und ermittelt die IP selbst, wenn das Feld leer ist. Danach kann das Kabel ab. Die Umschaltung gilt bis zum nächsten Neustart des Headsets; " +
            "PC und Headset müssen im selben Netz sein.",
            "Wi-Fi ADB: the headset has to be connected via USB once. When connecting, the tool switches ADB to Wi-Fi automatically (adb tcpip 5555) " +
            "and detects the IP itself if the field is empty. Afterwards the cable can be removed. The switch lasts until the headset restarts; " +
            "PC and headset must be on the same network.", muted: true);
        wifiHint.Margin = new Padding(0, 4, 0, 0);
        Ui.WrapTo(wifiHint, devices);
        devicesBox.Controls.Add(wifiHint);
        devices.SetContent(devicesBox);
        right.Controls.Add(devices);

        // --- Device info -----------------------------------------------
        var info = new Card("Geräteinformationen", "Device information") { Composited = true };
        var infoBox = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Color.Transparent, Dock = DockStyle.Top };
        infoBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _refreshInfo.Click += async (_, _) => await LoadDeviceInfoAsync();
        _copyInfo.Click += (_, _) =>
        {
            if (_info.Count == 0) return;
            string text = string.Join(Environment.NewLine, _info.Select(i => $"{Loc.T(i.De, i.En)}: {i.Value}"));
            try { Clipboard.SetText(text); } catch { }
        };
        _deviceInfoStatus.ForeColor = Theme.Muted;
        infoBox.Controls.Add(Ui.Row(_refreshInfo, _copyInfo, _deviceInfoStatus));
        _deviceInfo.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, 170);
        infoBox.Controls.Add(_deviceInfo);
        info.SetContent(infoBox);
        right.Controls.Add(info);
    }

    internal static TableLayoutPanel Column()
    {
        var column = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        column.ControlAdded += (_, e) => e.Control!.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        return column;
    }

    // ------------------------------------------------------------------

    public void OnActivated()
    {
        UpdateAdbInfo();
        if (_state.HasAdb && string.IsNullOrEmpty(_state.AdbVersionText))
            _ = RefreshVersionAsync();
        _ = LoadDeviceInfoAsync();
    }

    public void CancelOperation() { }

    private async Task RefreshVersionAsync()
    {
        await _state.RefreshVersionAsync();
        UpdateAdbInfo();
    }

    private void UpdateEnabled()
    {
        bool idle = !_state.IsBusy;
        _refreshInfo.Enabled = idle && _state.SelectedDevice?.IsReady == true;
        _connect.Enabled = idle && _state.HasAdb;
        _disconnect.Enabled = idle && _state.HasAdb;
        _german.Enabled = _english.Enabled = idle;
    }

    private void UpdateAdbInfo()
    {
        _adbPath.Text = _state.AdbPath ?? "";
        if (!_state.HasAdb)
        {
            _adbStatus.Text = Loc.T("✖ adb.exe nicht gefunden – bitte einrichten.", "✖ adb.exe not found – please set it up.");
            _adbStatus.ForeColor = Theme.Error;
            _adbVersion.Text = "-";
            _adbBuild.Text = "-";
        }
        else
        {
            _adbStatus.Text = Loc.T("✔ Einsatzbereit", "✔ Ready");
            _adbStatus.ForeColor = Theme.Accent;
            string[] lines = _state.AdbVersionText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _adbVersion.Text = lines.FirstOrDefault(l => l.StartsWith("Android Debug Bridge"))?.Replace("Android Debug Bridge version", "").Trim()
                               ?? (lines.Length > 0 ? lines[0] : Loc.T("wird ermittelt …", "checking …"));
            _adbBuild.Text = lines.FirstOrDefault(l => l.StartsWith("Version "))?.Replace("Version ", "") ?? "-";
        }
        UpdateEnabled();
    }

    private void UpdateDeviceList()
    {
        Control host = (Control?)_deviceList.Parent?.Parent ?? _deviceList;
        Ui.Frozen(host, UpdateDeviceListCore);
    }

    private void UpdateDeviceListCore()
    {
        _deviceList.SuspendLayout();
        foreach (Control old in _deviceList.Controls.Cast<Control>().ToList())
            old.Dispose();
        _deviceList.Controls.Clear();
        _deviceList.RowStyles.Clear();
        _deviceList.RowCount = 0;

        if (_state.Devices.Count == 0)
        {
            var none = Ui.Value(_state.HasAdb
                ? Loc.T("Kein Gerät gefunden. USB-Debugging, Datenkabel und Treiber prüfen; Gerät entsperren.",
                        "No device found. Check USB debugging, data cable and drivers; unlock the device.")
                : Loc.T("ADB ist noch nicht eingerichtet.", "ADB is not set up yet."));
            none.ForeColor = Theme.Muted;
            none.MaximumSize = new Size(Theme.S(420), 0);
            Ui.AddRow(_deviceList, none);
            _deviceList.SetColumnSpan(none, 4);
        }

        foreach (AdbDevice d in _state.Devices)
        {
            bool active = d.Serial == _state.SelectedDevice?.Serial;
            var dot = Ui.Value("●");
            dot.ForeColor = d.IsReady ? Theme.Accent : Theme.Warning;
            var name = Ui.Value(d.DisplayName + (active ? Loc.T("  (aktiv)", "  (active)") : ""));
            name.Font = active ? Theme.BoldFont : Theme.BaseFont;
            var serial = Ui.Value(d.Serial + (d.Serial.Contains(':') ? "  (WLAN)" : "  (USB)"));
            serial.ForeColor = Theme.Muted;
            var state = Ui.Value(d.StateText);
            state.ForeColor = d.IsReady ? Theme.Accent : Theme.Warning;
            foreach (Label l in new[] { dot, name, serial, state }) l.Margin = new Padding(0, 6, 14, 6);
            Ui.AddRow(_deviceList, dot, name, serial, state);
        }

        _deviceList.ResumeLayout();
        UpdateEnabled();
    }

    // ------------------------------------------------------------------

    private async Task LoadDeviceInfoAsync()
    {
        int request = ++_infoRequest;
        AdbDevice? device = _state.SelectedDevice;
        AdbClient? adb = _state.DeviceAdb;

        if (device is null || adb is null || !device.IsReady)
        {
            _info = new();
            _deviceInfoStatus.Text = device is null
                ? Loc.T("Kein Gerät ausgewählt.", "No device selected.")
                : Loc.T("Gerät nicht bereit.", "Device not ready.");
            RenderInfo();
            return;
        }
        if (_state.IsBusy)
        {
            _deviceInfoStatus.Text = Loc.T("Während eines laufenden Vorgangs nicht verfügbar.", "Not available while an operation is running.");
            return;
        }

        _deviceInfoStatus.Text = Loc.T("Lese Geräteinformationen …", "Reading device information …");
        try
        {
            const string sep = "@@ADBTOOL@@";
            string script = string.Join($"; echo {sep}; ",
                "getprop", "dumpsys battery", "df -h /data", "cat /proc/meminfo", "wm size", "wm density", "uptime", "ip -4 -o addr show");
            AdbResult result = await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(30), "shell", script);
            if (request != _infoRequest) return;
            if (!result.Ok && string.IsNullOrWhiteSpace(result.Output))
                throw new AdbException(result.Error);

            string[] parts = result.Output.Replace("\r", "").Split(sep);
            string Part(int i) => i < parts.Length ? parts[i].Trim() : "";

            var props = new Dictionary<string, string>();
            foreach (string line in Part(0).Split('\n'))
            {
                int close = line.IndexOf("]: [", StringComparison.Ordinal);
                if (line.StartsWith('[') && close > 0 && line.EndsWith(']'))
                    props[line[1..close]] = line[(close + 4)..^1];
            }
            string P(params string[] keys) => keys.Select(k => props.GetValueOrDefault(k, "")).FirstOrDefault(v => v.Length > 0) ?? "-";

            var battery = new Dictionary<string, string>();
            foreach (string line in Part(1).Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) battery[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            string batteryText = "-";
            if (battery.TryGetValue("level", out string? level))
            {
                batteryText = level + " %";
                if (battery.TryGetValue("temperature", out string? temp) && int.TryParse(temp, out int t))
                    batteryText += $" · {t / 10.0:0.0} °C";
                if (battery.TryGetValue("status", out string? status))
                    batteryText += status switch
                    {
                        "2" => Loc.T(" · lädt", " · charging"),
                        "5" => Loc.T(" · voll", " · full"),
                        _ => ""
                    };
            }

            string storage = "-";
            string[] df = Part(2).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (df.Length >= 2)
            {
                string[] cols = df[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length >= 5)
                    storage = Loc.T($"{cols[3]} frei von {cols[1]} ({cols[4]} belegt)", $"{cols[3]} free of {cols[1]} ({cols[4]} used)");
            }

            string ram = "-";
            string? memLine = Part(3).Split('\n').FirstOrDefault(l => l.StartsWith("MemTotal"));
            if (memLine is not null && long.TryParse(new string(memLine.Where(char.IsDigit).ToArray()), out long kb))
                ram = $"{kb / 1024.0 / 1024.0:0.0} GB";

            string display = string.Join(" · ", new[] { Part(4), Part(5) }
                .SelectMany(s => s.Split('\n'))
                .Select(s => s.Contains(':') ? s[(s.IndexOf(':') + 1)..].Trim() : s.Trim())
                .Where(s => s.Length > 0));

            // IPv4 addresses (without loopback), e.g. "7: wlan0    inet 192.168.1.50/24 brd ..."
            var addresses = new List<string>();
            foreach (string line in Part(7).Split('\n'))
            {
                string[] f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                int inet = Array.IndexOf(f, "inet");
                if (inet < 1 || inet + 1 >= f.Length) continue;
                string iface = f[1].TrimEnd(':');
                string ip = f[inet + 1].Split('/')[0];
                if (iface == "lo" || ip.StartsWith("127.")) continue;
                addresses.Add($"{ip} ({iface})");
            }
            string ipText = addresses.Count > 0
                ? string.Join(", ", addresses)
                : Loc.T("– (kein Netzwerk verbunden)", "– (no network connected)");

            string uptime = Part(6);
            int up = uptime.IndexOf("up ", StringComparison.Ordinal);
            if (up >= 0)
            {
                uptime = uptime[(up + 3)..];
                int users = uptime.IndexOf(",  ", StringComparison.Ordinal);
                if (users > 0) uptime = uptime[..users];
                uptime = uptime.Split(", load average")[0].Trim().TrimEnd(',');
            }

            string sdk = P("ro.build.version.sdk");
            _info = new List<(string, string, string)>
            {
                ("Hersteller", "Manufacturer", P("ro.product.manufacturer", "ro.product.vendor.manufacturer")),
                ("Modell", "Model", P("ro.product.model", "ro.product.vendor.model")),
                ("Gerät / Codename", "Device / codename", P("ro.product.device", "ro.product.vendor.device")),
                ("Seriennummer", "Serial number", device.Serial),
                ("Verbindung", "Connection", device.Serial.Contains(':') ? "WLAN / TCP" : "USB"),
                ("IPv4-Adresse", "IPv4 address", ipText),
                ("Android-Version", "Android version", $"{P("ro.build.version.release")} (API {sdk})"),
                ("Sicherheitspatch", "Security patch", P("ro.build.version.security_patch")),
                ("Build", "Build", P("ro.build.display.id")),
                ("Chipsatz", "Chipset", P("ro.soc.model", "ro.board.platform", "ro.hardware")),
                ("CPU-ABI", "CPU ABI", P("ro.product.cpu.abilist", "ro.product.cpu.abi")),
                ("Arbeitsspeicher", "Memory (RAM)", ram),
                ("Speicher (/data)", "Storage (/data)", storage),
                ("Akku", "Battery", batteryText),
                ("Display", "Display", display.Length > 0 ? display : "-"),
                ("Laufzeit", "Uptime", uptime.Length > 0 ? uptime : "-"),
            };
            _deviceInfoStatus.Text = Loc.T($"Stand: {DateTime.Now:HH:mm:ss}", $"As of {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            if (request != _infoRequest) return;
            _info = new();
            _deviceInfoStatus.Text = Loc.T("Fehler: ", "Error: ") + ex.Message;
        }
        RenderInfo();
    }

    private readonly List<(Label Key, Label Value)> _infoRows = new();

    /// <summary>
    /// Updates the device information table. Existing rows are reused and
    /// only their texts change, so a refresh does not rebuild (and flicker)
    /// the whole card.
    /// </summary>
    private void RenderInfo()
    {
        Control host = (Control?)_deviceInfo.Parent?.Parent ?? _deviceInfo;
        Ui.Frozen(host, () =>
        {
            _deviceInfo.SuspendLayout();
            while (_infoRows.Count < _info.Count)
            {
                var key = Ui.Value("");
                key.ForeColor = Theme.Muted;
                var val = Ui.Value("");
                val.MaximumSize = new Size(Theme.S(300), 0);
                key.Margin = val.Margin = new Padding(0, 4, 10, 4);
                Ui.AddRow(_deviceInfo, key, val);
                _infoRows.Add((key, val));
            }

            for (int i = 0; i < _infoRows.Count; i++)
            {
                bool used = i < _info.Count;
                var (key, val) = _infoRows[i];
                if (used)
                {
                    string k = Loc.T(_info[i].De, _info[i].En);
                    if (key.Text != k) key.Text = k;
                    if (val.Text != _info[i].Value) val.Text = _info[i].Value;
                }
                key.Visible = val.Visible = used;
            }
            _deviceInfo.ResumeLayout(true);
        });
        _copyInfo.Enabled = _info.Count > 0;
    }

    private void WifiStatus(string text, Color? color = null)
    {
        _wifiStatus.Text = text;
        _wifiStatus.ForeColor = color ?? Theme.Muted;
    }

    private static bool IsConnected(AdbResult r) =>
        r.Combined.Contains("connected to", StringComparison.OrdinalIgnoreCase) &&
        !r.Combined.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
        !r.Combined.Contains("cannot", StringComparison.OrdinalIgnoreCase) &&
        !r.Combined.Contains("unable", StringComparison.OrdinalIgnoreCase);

    /// <summary>IPv4 addresses (without loopback) of a device connected via USB.</summary>
    private static async Task<List<string>> DeviceIpsAsync(AdbClient adb)
    {
        var result = new List<string>();
        AdbResult r = await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "shell", "ip -4 -o addr show");
        foreach (string line in r.Output.Split('\n'))
        {
            string[] f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int inet = Array.IndexOf(f, "inet");
            if (inet < 1 || inet + 1 >= f.Length) continue;
            string ip = f[inet + 1].Split('/')[0];
            if (f[1].TrimEnd(':') != "lo" && !ip.StartsWith("127.")) result.Add(ip);
        }
        return result;
    }

    /// <summary>
    /// Connects via Wi-Fi. If the headset does not accept TCP connections yet,
    /// a USB-connected device is switched to TCP mode first ("adb tcpip PORT").
    /// </summary>
    private async Task ConnectAsync(bool connect)
    {
        AdbClient? adb = _state.Adb;
        if (adb is null) return;
        string address = _connectAddress.Text.Trim();

        if (!connect)
        {
            _connect.Enabled = _disconnect.Enabled = false;
            try
            {
                if (address.Length > 0 && !address.Contains(':')) address += ":5555";
                AdbResult r = await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(20),
                    address.Length > 0 ? new[] { "disconnect", address } : new[] { "disconnect" });
                WifiStatus(r.Combined.Trim());
                await _state.RefreshDevicesAsync();
            }
            catch (Exception ex) { WifiStatus(ex.Message, Theme.Error); }
            finally { UpdateEnabled(); }
            return;
        }

        if (!_state.TryBeginOperation(Loc.T("WLAN-Verbindung wird aufgebaut", "Connecting via Wi-Fi")))
            return;
        try
        {
            // USB devices that could be switched to Wi-Fi (selected one first)
            var usb = _state.Devices.Where(d => d.IsReady && !d.Serial.Contains(':'))
                .OrderByDescending(d => d.Serial == _state.SelectedDevice?.Serial).ToList();

            string host, port = "5555";
            AdbDevice? usbDevice = null;
            if (address.Length == 0)
            {
                usbDevice = usb.FirstOrDefault();
                if (usbDevice is null)
                {
                    WifiStatus(Loc.T("Bitte IP-Adresse eingeben oder das Headset einmal per USB anschliessen.",
                        "Please enter an IP address or connect the headset via USB once."), Theme.Warning);
                    return;
                }
                WifiStatus(Loc.T($"Ermittle IP-Adresse von {usbDevice.DisplayName} …", $"Detecting IP address of {usbDevice.DisplayName} …"));
                List<string> ips = await DeviceIpsAsync(adb.ForDevice(usbDevice.Serial));
                if (ips.Count == 0)
                {
                    WifiStatus(Loc.T($"{usbDevice.DisplayName} ist mit keinem Netzwerk verbunden (WLAN am Headset einschalten).",
                        $"{usbDevice.DisplayName} is not connected to a network (enable Wi-Fi on the headset)."), Theme.Warning);
                    return;
                }
                host = ips[0];
            }
            else
            {
                int colon = address.LastIndexOf(':');
                host = colon > 0 ? address[..colon] : address;
                if (colon > 0) port = address[(colon + 1)..];
                if (!int.TryParse(port, out int p) || p is < 1 or > 65535)
                {
                    WifiStatus(Loc.T("Ungültiger Port.", "Invalid port."), Theme.Error);
                    return;
                }
            }
            string target = $"{host}:{port}";
            _connectAddress.Text = target;

            // A stale (offline) entry would answer "already connected" – remove it first.
            if (_state.Devices.FirstOrDefault(d => d.Serial == target) is { IsReady: false })
                await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "disconnect", target);

            // 1st attempt: maybe TCP mode is already active
            WifiStatus(Loc.T($"Verbinde mit {target} …", $"Connecting to {target} …"));
            AdbResult result = await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(15), "connect", target);

            if (!IsConnected(result))
            {
                // Find the USB device that owns this IP (or the only/selected USB device)
                if (usbDevice is null)
                {
                    foreach (AdbDevice d in usb)
                    {
                        try
                        {
                            if ((await DeviceIpsAsync(adb.ForDevice(d.Serial))).Contains(host)) { usbDevice = d; break; }
                        }
                        catch { }
                    }
                    usbDevice ??= usb.Count == 1 ? usb[0] : null;
                }

                if (usbDevice is null)
                {
                    WifiStatus(Loc.T(
                        $"Keine Verbindung zu {target}. WLAN-ADB ist am Headset nicht aktiv – Headset einmal per USB anschliessen, dann erneut „WLAN verbinden“.\n{result.Combined.Trim()}",
                        $"Could not connect to {target}. Wi-Fi ADB is not active on the headset – connect it via USB once, then click \"Connect Wi-Fi\" again.\n{result.Combined.Trim()}"),
                        Theme.Error);
                    return;
                }

                WifiStatus(Loc.T($"Schalte ADB auf {usbDevice.DisplayName} auf WLAN um (adb tcpip {port}) …",
                                 $"Switching ADB on {usbDevice.DisplayName} to Wi-Fi (adb tcpip {port}) …"));
                AdbResult tcp = await adb.ForDevice(usbDevice.Serial).CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(15), "tcpip", port);
                if (!tcp.Ok)
                {
                    WifiStatus(Loc.T($"Umschalten fehlgeschlagen: {tcp.Combined.Trim()}", $"Switching failed: {tcp.Combined.Trim()}"), Theme.Error);
                    return;
                }

                // adbd restarts on the device – retry for a few seconds
                for (int attempt = 1; attempt <= 8 && !IsConnected(result); attempt++)
                {
                    await Task.Delay(attempt == 1 ? 2500 : 1000);
                    WifiStatus(Loc.T($"Verbinde mit {target} … (Versuch {attempt})", $"Connecting to {target} … (attempt {attempt})"));
                    result = await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "connect", target);
                }
            }

            await _state.RefreshDevicesAsync();
            if (IsConnected(result) && _state.Devices.FirstOrDefault(d => d.Serial == target) is { IsReady: false })
            {
                // Connected but not (yet) authorized/online – give it a moment.
                await Task.Delay(1500);
                await _state.RefreshDevicesAsync();
            }
            if (IsConnected(result))
            {
                AdbDevice? wifi = _state.Devices.FirstOrDefault(d => d.Serial == target);
                if (wifi is not null) _state.SelectDevice(wifi);
                WifiStatus(Loc.T($"✔ Verbunden mit {target} – das USB-Kabel kann jetzt abgezogen werden.",
                                 $"✔ Connected to {target} – the USB cable can be removed now."), Theme.Accent);
            }
            else
            {
                WifiStatus(Loc.T($"Keine Verbindung zu {target}. PC und Headset im selben Netz? (Gäste-WLAN / Client-Isolation verhindern die Verbindung)\n{result.Combined.Trim()}",
                                 $"Could not connect to {target}. Are PC and headset on the same network? (guest Wi-Fi / client isolation block it)\n{result.Combined.Trim()}"),
                    Theme.Error);
            }
        }
        catch (Exception ex)
        {
            WifiStatus(ex.Message, Theme.Error);
        }
        finally
        {
            _state.EndOperation();
            UpdateEnabled();
        }
    }

    // ------------------------------------------------------------------

    private void ChooseAdb()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Loc.T("ADB auswählen", "Choose ADB"),
            Filter = Loc.T("Android Debug Bridge (adb.exe)|adb.exe|Alle Dateien (*.*)|*.*", "Android Debug Bridge (adb.exe)|adb.exe|All files (*.*)|*.*"),
            CheckFileExists = true
        };
        if (_state.AdbPath is { } current && File.Exists(current))
            dialog.InitialDirectory = Path.GetDirectoryName(current);

        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            _state.SetAdbPath(dialog.FileName);
    }

    public void ShowSetupHelp(bool missing)
    {
        using Form dialog = Ui.Dialog(Loc.T("ADB einrichten", "Set up ADB"), 760, 560);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.S(40),
            Font = Theme.SectionFont,
            Text = missing
                ? Loc.T("ADB fehlt – in wenigen Schritten startklar", "ADB is missing – let's get you started")
                : Loc.T("ADB und Android-Verbindung einrichten", "Set up ADB and your Android connection")
        };
        var text = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Text = Loc.T(
                "1. Öffne die offizielle Google-Downloadseite und lade die SDK Platform-Tools für Windows herunter.\n\n" +
                "2. Entpacke das gesamte ZIP, z. B. nach C:\\Android. adb.exe und die mitgelieferten DLL-Dateien müssen zusammenbleiben.\n\n" +
                "3. Klicke unten auf ‚adb.exe auswählen‘. Eine Änderung des Windows-PATH ist nicht nötig. " +
                "Alternativ den Ordner als „adb“ neben ADBora.exe ablegen – er wird automatisch erkannt.\n\n" +
                "4. Aktiviere auf Android die Entwickleroptionen (meist siebenmal auf die Build-Nummer tippen) und USB-Debugging.\n\n" +
                "5. Verbinde das Gerät per USB-Datenkabel, entsperre es und bestätige den RSA-Dialog. Bei Bedarf den USB-Treiber des Geräteherstellers installieren.",
                "1. Open Google's official download page and download SDK Platform-Tools for Windows.\n\n" +
                "2. Extract the entire ZIP, e.g. to C:\\Android. Keep adb.exe and the supplied DLL files together.\n\n" +
                "3. Click 'Choose adb.exe' below. You do not need to change the Windows PATH. " +
                "Alternatively place the folder as \"adb\" next to ADBora.exe – it is detected automatically.\n\n" +
                "4. Enable Developer options on Android (usually tap the build number seven times), then enable USB debugging.\n\n" +
                "5. Connect a USB data cable, unlock the device and accept the RSA prompt. Install the device manufacturer's USB driver if needed.")
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Color.Transparent, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, Theme.S(10), 0, 0) };
        var download = Ui.Button("Platform-Tools herunterladen", "Download Platform-Tools", ButtonKind.Primary);
        var choose = Ui.Button("adb.exe auswählen …", "Choose adb.exe …");
        var close = Ui.Button("Schließen", "Close");
        download.Click += (_, _) => OpenUrl(AdbLocator.PlatformToolsUrl + (Loc.IsEnglish ? "?hl=en" : "?hl=de"));
        choose.Click += (_, _) => { dialog.Close(); ChooseAdb(); };
        close.Click += (_, _) => dialog.Close();
        buttons.Controls.AddRange(new Control[] { download, choose, close });

        dialog.Controls.Add(text);
        dialog.Controls.Add(buttons);
        dialog.Controls.Add(title);
        Ui.ShowDialog(dialog, FindForm());
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }
}
