using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>Tab 1: language, ADB setup, installed versions and device information.</summary>
internal sealed class SettingsPage : ScrollViewer, IPage
{
    private readonly AppState _state;

    private readonly TextBlock _languageName = new() { Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _language = Ui.Button("Sprache ändern", "Change language");
    private readonly CheckBox _stopServer = Ui.CheckBox("ADB-Server beim Beenden stoppen", "Stop ADB server on exit");
    private readonly CheckBox _hideSerial = Ui.CheckBox("Seriennummern und Benutzerordner ausblenden (z. B. für Screenshots)", "Hide serial numbers and user folder (e.g. for screenshots)");

    private readonly TextBox _adbPath = new() { IsReadOnly = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _adbStatus = Ui.Value();
    private readonly TextBlock _adbVersion = Ui.Value();
    private readonly TextBlock _adbBuild = Ui.Value();
    private readonly TextBlock _toolVersion = Ui.Value();
    private readonly TextBlock _dotnetVersion = Ui.Value();
    private readonly TextBlock _osVersion = Ui.Value();
    private readonly TextBlock _dataMode = Ui.Value();

    private readonly FormGrid _deviceList = Ui.Grid(4);
    private readonly FormGrid _deviceInfo = Ui.Grid(2);
    private readonly TextBlock _deviceInfoStatus = Ui.Value("");
    private readonly TextBox _connectAddress = Ui.TextBox(260);
    private string? _autoAddress; // last address ADBora put into _connectAddress itself
    private readonly Button _refreshInfo = Ui.Button("Infos aktualisieren", "Refresh info");
    private readonly Button _copyInfo = Ui.Button("Kopieren", "Copy");
    private readonly Button _connect = Ui.Button("WLAN verbinden", "Connect Wi-Fi");
    private readonly Button _disconnect = Ui.Button("Trennen", "Disconnect");
    private readonly TextBlock _wifiStatus = Ui.Value("");

    private List<(string De, string En, string Value)> _info = new();
    private int _infoRequest;

    public SettingsPage(AppState state)
    {
        _state = state;
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;

        BuildUi();

        _state.AdbChanged += UpdateAdbInfo;
        _state.DevicesChanged += () => { UpdateDeviceList(); SyncConnectAddress(); };
        _state.SelectedDeviceChanged += () => { UpdateDeviceList(); SyncConnectAddress(); if (IsVisible) _ = LoadDeviceInfoAsync(); };
        _state.BusyChanged += UpdateEnabled;
        Loc.LanguageChanged += () => { UpdateAdbInfo(); UpdateDeviceList(); RenderInfo(); };

        UpdateAdbInfo();
        UpdateDeviceList();
        UpdateEnabled();
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private void BuildUi()
    {
        var left = new StackPanel();
        var right = new StackPanel();
        Content = Responsive.TwoColumns(left, right, breakpoint: 980, padding: new Thickness(20, 16));

        // --- General ---------------------------------------------------
        var general = new Card("Allgemein", "General");
        var generalGrid = Ui.Grid(2);
        void ShowLanguageName() => _languageName.Text = Loc.Language switch
        {
            "de" => "Deutsch",
            "en" => "English",
            string code => Languages.Load(code)?.Name ?? code
        };
        ShowLanguageName();
        Loc.LanguageChanged += ShowLanguageName;
        _language.Click += async (_, _) =>
        {
            string? lang = await LanguageDialog.Show(Owner);
            if (lang is null) return;
            _state.Settings.Language = lang;
            _state.Settings.Save();
            Loc.SetLanguage(lang);
        };
        _stopServer.IsChecked = _state.Settings.StopAdbServerOnExit;
        _stopServer.IsCheckedChanged += (_, _) =>
        {
            _state.Settings.StopAdbServerOnExit = _stopServer.IsChecked == true;
            _state.Settings.Save();
        };
        Ui.AddRow(generalGrid, Ui.Label("Sprache / Language", "Language / Sprache", muted: true), Ui.Row(_languageName, _language));
        Ui.AddRow(generalGrid, Ui.Spacer(), _stopServer);
        _hideSerial.IsChecked = _state.Settings.HideSerial;
        _hideSerial.IsCheckedChanged += (_, _) =>
        {
            _state.Settings.HideSerial = _hideSerial.IsChecked == true;
            _state.Settings.Save();
            Privacy.HideSerial = _hideSerial.IsChecked == true;
        };
        Privacy.Changed += () => { UpdateDeviceList(); RenderDataMode(); UpdateAdbInfo(); if (IsVisible) _ = LoadDeviceInfoAsync(); };
        Ui.AddRow(generalGrid, Ui.Spacer(), _hideSerial);
        Ui.AddRow(generalGrid, Ui.Label("ADBora", "ADBora", muted: true), _toolVersion);
        Ui.AddRow(generalGrid, Ui.Label(".NET-Laufzeit", ".NET runtime", muted: true), _dotnetVersion);
        Ui.AddRow(generalGrid, Ui.Label("Betriebssystem", "Operating system", muted: true), _osVersion);
        Ui.AddRow(generalGrid, Ui.Label("Datenablage", "Data storage", muted: true), _dataMode);
        void RenderDataMode() => _dataMode.Text = (AppSettings.IsPortable
            ? Loc.T("Portabel", "Portable")
            : Loc.T("Installiert", "Installed")) + " · " + Privacy.MaskPaths(AppSettings.DataDirectory);
        RenderDataMode();
        Loc.LanguageChanged += RenderDataMode;
        var dataFolder = Ui.Button("Datenordner öffnen", "Open data folder");
        dataFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            Platform.OpenFolder(AppSettings.DataDirectory);
        };
        Ui.AddRow(generalGrid, Ui.Spacer(), Ui.Row(dataFolder));
        general.SetContent(generalGrid);
        left.Children.Add(general);

        _toolVersion.Text = AppInfo.Version;
        _dotnetVersion.Text = RuntimeInformation.FrameworkDescription;
        _osVersion.Text = $"{OsName()} ({RuntimeInformation.OSArchitecture})";

        // --- ADB -------------------------------------------------------
        string adbExe = Platform.Exe("adb");
        var adb = new Card("ADB (Android Debug Bridge)", "ADB (Android Debug Bridge)");
        var adbGrid = Ui.Grid(2);
        Ui.AddRow(adbGrid, Ui.Label(adbExe, adbExe, muted: true), _adbPath);

        var choose = Ui.Button($"{adbExe} auswählen …", $"Choose {adbExe} …");
        var search = Ui.Button("Automatisch suchen", "Search automatically");
        var download = Ui.Button("Platform-Tools herunterladen", "Download Platform-Tools");
        var help = Ui.Button("Einrichtungshilfe", "Setup help");
        choose.Click += async (_, _) => await ChooseAdb();
        search.Click += async (_, _) =>
        {
            _state.Settings.AdbPath = "";
            _state.RediscoverAdb();
            if (!_state.HasAdb)
                await Dialogs.Message(Owner, Loc.T($"{adbExe} wurde nicht gefunden.", $"{adbExe} was not found."));
        };
        download.Click += (_, _) => Platform.Open(AdbLocator.PlatformToolsUrl + ("?hl=" + Loc.Language));
        help.Click += async (_, _) => await ShowSetupHelp(missing: false);
        var buttons = new StackPanel { Margin = new Thickness(0, 4, 0, 4), Children = { Ui.Row(choose, search), Ui.Row(download, help) } };
        Ui.AddRow(adbGrid, Ui.Spacer(), buttons);
        Ui.AddRow(adbGrid, Ui.Label("Status", "Status", muted: true), _adbStatus);
        Ui.AddRow(adbGrid, Ui.Label("ADB-Version", "ADB version", muted: true), _adbVersion);
        Ui.AddRow(adbGrid, Ui.Label("Platform-Tools", "Platform-Tools", muted: true), _adbBuild);
        adb.SetContent(adbGrid);
        left.Children.Add(adb);

        // --- Devices ---------------------------------------------------
        var devices = new Card("Angeschlossene Geräte", "Connected devices");
        devices.SetContent(_deviceList);
        devices.SetContent(Ui.Label("Das aktive Gerät wird oben rechts gewählt und gilt für alle Tabs.",
            "The active device is selected at the top right and applies to all tabs.", muted: true));

        Ui.Placeholder(_connectAddress, "IP[:Port] – leer = IP des USB-Geräts", "IP[:port] – empty = IP of the USB device");
        _connectAddress.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (_connect.IsEnabled) await ConnectAsync(true);
        };
        _connect.Click += async (_, _) => await ConnectAsync(true);
        _disconnect.Click += async (_, _) => await ConnectAsync(false);
        devices.SetContent(Ui.Row(_connectAddress, _connect, _disconnect));
        _wifiStatus.Foreground = AppTheme.Muted;
        _wifiStatus.Margin = new Thickness(0, 2, 0, 4);
        devices.SetContent(_wifiStatus);
        var wifiHint = Ui.Label(
            "WLAN-ADB: Das Headset muss einmal per USB angeschlossen sein. Beim Verbinden schaltet das Tool ADB automatisch auf WLAN um (adb tcpip 5555) " +
            "und ermittelt die IP selbst, wenn das Feld leer ist. Danach kann das Kabel ab. Die Umschaltung gilt bis zum nächsten Neustart des Headsets; " +
            "PC und Headset müssen im selben Netz sein.",
            "Wi-Fi ADB: the headset has to be connected via USB once. When connecting, the tool switches ADB to Wi-Fi automatically (adb tcpip 5555) " +
            "and detects the IP itself if the field is empty. Afterwards the cable can be removed. The switch lasts until the headset restarts; " +
            "PC and headset must be on the same network.", muted: true);
        wifiHint.Margin = new Thickness(0, 4, 0, 0);
        devices.SetContent(wifiHint);
        right.Children.Add(devices);

        // --- Device info -----------------------------------------------
        var info = new Card("Geräteinformationen", "Device information");
        _refreshInfo.Click += async (_, _) => await LoadDeviceInfoAsync();
        _copyInfo.Click += async (_, _) =>
        {
            if (_info.Count == 0) return;
            string text = string.Join(Environment.NewLine, _info.Select(i => $"{Loc.T(i.De, i.En)}: {i.Value}"));
            await Dialogs.CopyText(this, text);
        };
        _deviceInfoStatus.Foreground = AppTheme.Muted;
        info.SetContent(Ui.Row(_refreshInfo, _copyInfo, _deviceInfoStatus));
        _deviceInfo.ColumnDefinitions[0].Width = new GridLength(170);
        _deviceInfo.Margin = new Thickness(0, 6, 0, 0);
        info.SetContent(_deviceInfo);
        right.Children.Add(info);
    }

    /// <summary>"Microsoft Windows 10.0.26100" or the distribution name from /etc/os-release ("CachyOS Linux").</summary>
    private static string OsName()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                string? pretty = File.ReadLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME="));
                if (pretty is not null)
                    return pretty[12..].Trim('"') + " · Kernel " + Environment.OSVersion.Version;
            }
            catch { }
        }
        return RuntimeInformation.OSDescription;
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
        _refreshInfo.IsEnabled = idle && _state.SelectedDevice?.IsReady == true;
        _connect.IsEnabled = idle && _state.HasAdb;
        _disconnect.IsEnabled = idle && _state.HasAdb;
        _language.IsEnabled = idle;
    }

    private void UpdateAdbInfo()
    {
        _adbPath.Text = Privacy.MaskPaths(_state.AdbPath ?? "");
        if (!_state.HasAdb)
        {
            string adbExe = Platform.Exe("adb");
            _adbStatus.Text = Loc.T($"✖ {adbExe} nicht gefunden – bitte einrichten.", $"✖ {adbExe} not found – please set it up.");
            _adbStatus.Foreground = AppTheme.Error;
            _adbVersion.Text = "-";
            _adbBuild.Text = "-";
        }
        else
        {
            _adbStatus.Text = Loc.T("✔ Einsatzbereit", "✔ Ready");
            _adbStatus.Foreground = AppTheme.Accent;
            string[] lines = _state.AdbVersionText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _adbVersion.Text = lines.FirstOrDefault(l => l.StartsWith("Android Debug Bridge"))?.Replace("Android Debug Bridge version", "").Trim()
                               ?? (lines.Length > 0 ? lines[0] : Loc.T("wird ermittelt …", "checking …"));
            _adbBuild.Text = lines.FirstOrDefault(l => l.StartsWith("Version "))?.Replace("Version ", "") ?? "-";
        }
        UpdateEnabled();
    }

    private void UpdateDeviceList()
    {
        _deviceList.Clear();

        if (_state.Devices.Count == 0)
        {
            var none = Ui.Value(_state.HasAdb
                ? Loc.T("Kein Gerät gefunden. USB-Debugging, Datenkabel und Treiber prüfen; Gerät entsperren.",
                        "No device found. Check USB debugging, data cable and drivers; unlock the device.")
                : Loc.T("ADB ist noch nicht eingerichtet.", "ADB is not set up yet."));
            none.Foreground = AppTheme.Muted;
            _deviceList.AddRow(none);
        }

        foreach (AdbDevice d in _state.Devices)
        {
            bool active = d.Serial == _state.SelectedDevice?.Serial;
            var dot = Ui.Value("●");
            dot.Foreground = d.IsReady ? AppTheme.Accent : AppTheme.Warning;
            var name = Ui.Value(d.DisplayName + (active ? Loc.T("  (aktiv)", "  (active)") : ""));
            if (active) name.FontWeight = FontWeight.SemiBold;
            var serial = Ui.Value(d.ShownSerial + (d.Serial.Contains(':') ? "  (WLAN)" : "  (USB)"));
            serial.Foreground = AppTheme.Muted;
            var state = Ui.Value(d.StateText);
            state.Foreground = d.IsReady ? AppTheme.Accent : AppTheme.Warning;
            foreach (TextBlock l in new[] { dot, name, serial, state }) { l.Margin = new Thickness(0, 4, 14, 4); l.TextWrapping = TextWrapping.NoWrap; }
            _deviceList.AddRow(dot, name, serial, state);
        }
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
                "getprop", "dumpsys battery", "df -h /data", "cat /proc/meminfo", "wm size", "wm density", "uptime", "ip -4 -o addr show",
                "dumpsys usb",
                // USB interfaces bound to a network driver (r8152, cdc_ncm, ax88179_178a, …)
                "for d in /sys/bus/usb/drivers/*; do for i in $d/*:*; do [ -e \"$i\" ] && echo \"${d##*/} ${i##*/}\"; done; done",
                // link speed from the kernel log (readable on the Quest)
                "logcat -d -b all -t 3000 2>/dev/null | grep -E 'eth[0-9]+: [0-9]+ mbit/s' | tail -1");
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
                // Quests still report "Oculus"; the brand has been Meta for years.
                ("Hersteller", "Manufacturer", P("ro.product.manufacturer", "ro.product.vendor.manufacturer") is var maker
                    && maker.Equals("Oculus", StringComparison.OrdinalIgnoreCase) ? "Meta" : maker),
                ("Modell", "Model", P("ro.product.model", "ro.product.vendor.model")),
                ("Gerät / Codename", "Device / codename", P("ro.product.device", "ro.product.vendor.device")),
                ("Seriennummer", "Serial number", device.ShownSerial),
                ("Verbindung", "Connection", device.Serial.Contains(':') ? "WLAN / TCP" : "USB"),
                ("IPv4-Adresse", "IPv4 address", ipText),
                ("Android-Version", "Android version", $"{P("ro.build.version.release")} (API {sdk})"),
                ("Sicherheitspatch", "Security patch", P("ro.build.version.security_patch")),
                ("Build", "Build", int.TryParse(P("ro.vros.build.version"), out int hzos) && hzos > 0
                    ? $"Horizon OS {(hzos >= 200 ? $"v{hzos / 100}.{hzos % 100}" : $"v{hzos}")} ({P("ro.build.display.id")})" // 209 → v2.9
                    : P("ro.build.display.id")),
                ("Chipsatz", "Chipset", P("ro.soc.model", "ro.board.platform", "ro.hardware")),
                ("CPU-ABI", "CPU ABI", P("ro.product.cpu.abilist", "ro.product.cpu.abi")),
                ("Arbeitsspeicher", "Memory (RAM)", ram),
                ("Speicher (/data)", "Storage (/data)", storage),
                ("Akku", "Battery", batteryText),
                ("Display", "Display", display.Length > 0 ? display : "-"),
                ("Laufzeit", "Uptime", uptime.Length > 0 ? uptime : "-"),
            };
            _info.AddRange(EthernetInfo(Part(7), Part(8), Part(9), Part(10), device.Serial.Contains(':')));
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

    private readonly List<(TextBlock Key, TextBlock Value)> _infoRows = new();

    /// <summary>Updates the device information table; existing rows are reused, only their texts change.</summary>
    private void RenderInfo()
    {
        while (_infoRows.Count < _info.Count)
        {
            var key = Ui.Value("");
            key.Foreground = AppTheme.Muted;
            var val = Ui.Value("");
            key.Margin = val.Margin = new Thickness(0, 3, 10, 3);
            _deviceInfo.AddRow(key, val);
            _infoRows.Add((key, val));
        }

        for (int i = 0; i < _infoRows.Count; i++)
        {
            bool used = i < _info.Count;
            var (key, val) = _infoRows[i];
            if (used)
            {
                key.Text = Loc.T(_info[i].De, _info[i].En);
                val.Text = _info[i].Value;
            }
            key.IsVisible = val.IsVisible = used;
        }
        _copyInfo.IsEnabled = _info.Count > 0;
    }

    private void WifiStatus(string text, IBrush? color = null)
    {
        _wifiStatus.Text = text;
        _wifiStatus.Foreground = color ?? AppTheme.Muted;
    }

    private static bool IsConnected(AdbResult r) =>
        r.Combined.Contains("connected to", StringComparison.OrdinalIgnoreCase) &&
        !r.Combined.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
        !r.Combined.Contains("cannot", StringComparison.OrdinalIgnoreCase) &&
        !r.Combined.Contains("unable", StringComparison.OrdinalIgnoreCase);

    /// <summary>IPv4 addresses (without loopback) of a device connected via USB.</summary>
    private static readonly Dictionary<string, string> EthernetChips = new()
    {
        ["0BDA:8150"] = "Realtek RTL8150 · 100 Mbit/s", ["0BDA:8152"] = "Realtek RTL8152 · 100 Mbit/s",
        ["0BDA:8153"] = "Realtek RTL8153 · 1 Gbit/s",   ["0BDA:8155"] = "Realtek RTL8155 · 2.5 Gbit/s",
        ["0BDA:8156"] = "Realtek RTL8156 · 2.5 Gbit/s", ["0BDA:8157"] = "Realtek RTL8157 · 5 Gbit/s",
        ["0BDA:8159"] = "Realtek RTL8159 · 10 Gbit/s",
        ["0B95:1790"] = "ASIX AX88179 · 1 Gbit/s",      ["0B95:178A"] = "ASIX AX88178A · 1 Gbit/s",
        ["0B95:7720"] = "ASIX AX88772 · 100 Mbit/s",    ["0B95:772A"] = "ASIX AX88772A · 100 Mbit/s",
        ["0B95:772B"] = "ASIX AX88772B · 100 Mbit/s",   ["0B95:7E2B"] = "ASIX AX88772C · 100 Mbit/s",
        ["2EB9:1700"] = "Aquantia AQC111U · 5 Gbit/s",
    };

    private static readonly string[] NetDrivers = { "r8152", "cdc_ncm", "cdc_ether", "cdc_eem", "ax88179_178a", "asix", "aqc111", "rtl8150" };

    /// <summary>
    /// USB Ethernet adapter on the headset (for support): name and chip from "dumpsys usb"
    /// (host devices), driver from /sys/bus/usb/drivers, link speed from the kernel log if
    /// readable. Only shown when an eth interface has an address – plus a hint when the
    /// USB-C port is in charging mode (an adapter with power pass-through is then ignored).
    /// </summary>
    private static IEnumerable<(string, string, string)> EthernetInfo(string ipAddr, string usb, string drivers, string kernel, bool networkAdb)
    {
        var eth = Regex.Matches(ipAddr, @"^\d+:\s+(eth\d+)\s+inet\s+([\d.]+)", RegexOptions.Multiline)
            .Select(m => (If: m.Groups[1].Value, Ip: m.Groups[2].Value)).ToList();
        if (eth.Count == 0)
        {
            if (networkAdb && Regex.IsMatch(usb, @"current_mode=ufp") && Regex.IsMatch(usb, @"sink_power=true") && Regex.IsMatch(usb, @"host_connected=false"))
                yield return ("USB-C-Port", "USB-C port", Loc.T(
                    "Lademodus – ein Ethernet-Adapter mit Ladeanschluss wird so nicht erkannt; Adapter zuerst ohne Ladegerät einstecken.",
                    "Charging mode – an Ethernet adapter with a charging port is not detected this way; plug in the adapter without the charger first."));
            yield break;
        }

        // host devices: "name=/dev/bus/usb/… vendor_id=… product_id=… class=… manufacturer_name=… product_name=…"
        int hm = usb.IndexOf("host_manager={", StringComparison.Ordinal);
        string host = hm >= 0 ? usb[hm..] : "";
        var devices = Regex.Matches(host, @"name=/dev/bus/usb/\S+\s+vendor_id=(\d+)\s+product_id=(\d+)\s+class=(\d+)[\s\S]*?manufacturer_name=([^\n]*)\n\s*product_name=([^\n]*)")
            .Select(m => (Id: $"{int.Parse(m.Groups[1].Value):X4}:{int.Parse(m.Groups[2].Value):X4}", Class: m.Groups[3].Value,
                          Name: $"{m.Groups[4].Value.Trim()} {m.Groups[5].Value.Trim()}".Replace("null", "").Trim()))
            .Where(d => d.Class != "9") // hubs
            .ToList();
        var adapter = devices.FirstOrDefault(d => EthernetChips.ContainsKey(d.Id));
        if (adapter.Id is null) adapter = devices.FirstOrDefault(d => Regex.IsMatch(d.Name, "LAN|Ethernet|Network|GbE", RegexOptions.IgnoreCase));
        if (adapter.Id is null && devices.Count == 1) adapter = devices[0];
        if (adapter.Id is not null)
        {
            string chip = EthernetChips.TryGetValue(adapter.Id, out string? c) ? c : "";
            yield return ("USB-Ethernet-Adapter", "USB Ethernet adapter",
                string.Join(" · ", new[] { adapter.Name, chip, adapter.Id }.Where(x => x.Length > 0)));
        }

        string driver = drivers.Split('\n').Select(l => l.Trim().Split(' ')[0]).FirstOrDefault(d => NetDrivers.Contains(d)) ?? "";
        foreach (var e in eth)
        {
            Match speed = Regex.Match(kernel, $@"{e.If}: (\d+) mbit/s");
            string link = speed.Success ? (int.Parse(speed.Groups[1].Value) is int mb && mb >= 1000 ? $"{mb / 1000.0:0.#} Gbit/s" : $"{speed.Groups[1].Value} Mbit/s") : "";
            yield return ("Ethernet-Verbindung", "Ethernet connection",
                string.Join(" · ", new[] { e.If, e.Ip, link, driver.Length > 0 ? Loc.T("Treiber", "driver") + " " + driver : "" }.Where(x => x.Length > 0)));
        }
    }

    /// <summary>
    /// Address field follows the selection: the address of the selected Wi-Fi device, empty for a
    /// USB device (= "use its IP"). An address the user typed in is left alone.
    /// </summary>
    private void SyncConnectAddress()
    {
        string current = (_connectAddress.Text ?? "").Trim();
        if (current.Length > 0 && current != _autoAddress) return;
        AdbDevice? sel = _state.SelectedDevice;
        string next = sel is { IsReady: true } && sel.Serial.Contains(':') ? sel.Serial : "";
        if (next == current) return;
        _connectAddress.Text = next;
        _autoAddress = next.Length > 0 ? next : null;
    }

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
        string address = (_connectAddress.Text ?? "").Trim();

        if (!connect)
        {
            _connect.IsEnabled = _disconnect.IsEnabled = false;
            try
            {
                if (address.Length > 0 && !address.Contains(':')) address += ":5555";
                AdbResult r = await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(20),
                    address.Length > 0 ? new[] { "disconnect", address } : new[] { "disconnect" });
                WifiStatus(r.Combined.Trim());
                await _state.RefreshDevicesAsync();
            }
            catch (Exception ex) { WifiStatus(ex.Message, AppTheme.Error); }
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

            // The field still shows the address of an earlier connection (e.g. a second headset is
            // now on USB): if that device is already connected via Wi-Fi, or ADBora filled the field
            // itself, use the IP of the USB device instead.
            if (address.Length > 0 && usb.Count > 0)
            {
                string normalized = address.Contains(':') ? address : address + ":5555";
                if (normalized == _autoAddress || _state.Devices.Any(d => d.IsReady && d.Serial == normalized))
                    address = "";
            }

            string host, port = "5555";
            AdbDevice? usbDevice = null;
            if (address.Length == 0)
            {
                usbDevice = usb.FirstOrDefault();
                if (usbDevice is null)
                {
                    WifiStatus(Loc.T("Bitte IP-Adresse eingeben oder das Headset einmal per USB anschliessen.",
                        "Please enter an IP address or connect the headset via USB once."), AppTheme.Warning);
                    return;
                }
                WifiStatus(Loc.T($"Ermittle IP-Adresse von {usbDevice.DisplayName} …", $"Detecting IP address of {usbDevice.DisplayName} …"));
                List<string> ips = await DeviceIpsAsync(adb.ForDevice(usbDevice.Serial));
                if (ips.Count == 0)
                {
                    WifiStatus(Loc.T($"{usbDevice.DisplayName} ist mit keinem Netzwerk verbunden (WLAN am Headset einschalten).",
                        $"{usbDevice.DisplayName} is not connected to a network (enable Wi-Fi on the headset)."), AppTheme.Warning);
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
                    WifiStatus(Loc.T("Ungültiger Port.", "Invalid port."), AppTheme.Error);
                    return;
                }
            }
            string target = $"{host}:{port}";
            _connectAddress.Text = _autoAddress = target;

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
                        AppTheme.Error);
                    return;
                }

                WifiStatus(Loc.T($"Schalte ADB auf {usbDevice.DisplayName} auf WLAN um (adb tcpip {port}) …",
                                 $"Switching ADB on {usbDevice.DisplayName} to Wi-Fi (adb tcpip {port}) …"));
                AdbResult tcp = await adb.ForDevice(usbDevice.Serial).CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(15), "tcpip", port);
                if (!tcp.Ok)
                {
                    WifiStatus(Loc.T($"Umschalten fehlgeschlagen: {tcp.Combined.Trim()}", $"Switching failed: {tcp.Combined.Trim()}"), AppTheme.Error);
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
                                 $"✔ Connected to {target} – the USB cable can be removed now."), AppTheme.Accent);
            }
            else
            {
                WifiStatus(Loc.T($"Keine Verbindung zu {target}. PC und Headset im selben Netz? (Gäste-WLAN / Client-Isolation verhindern die Verbindung)\n{result.Combined.Trim()}",
                                 $"Could not connect to {target}. Are PC and headset on the same network? (guest Wi-Fi / client isolation block it)\n{result.Combined.Trim()}"),
                    AppTheme.Error);
            }
        }
        catch (Exception ex)
        {
            WifiStatus(ex.Message, AppTheme.Error);
        }
        finally
        {
            _state.EndOperation();
            UpdateEnabled();
        }
    }

    // ------------------------------------------------------------------

    private async Task ChooseAdb()
    {
        if (Owner is not { } owner) return;
        string adbExe = Platform.Exe("adb");
        string? file = await Dialogs.OpenFile(owner, Loc.T("ADB auswählen", "Choose ADB"),
            _state.AdbPath is { } current && File.Exists(current) ? Path.GetDirectoryName(current) : null,
            ($"Android Debug Bridge ({adbExe})", new[] { adbExe }), (Loc.T("Alle Dateien", "All files"), new[] { "*" }));
        if (file is not null)
            _state.SetAdbPath(file);
    }

    public async Task ShowSetupHelp(bool missing)
    {
        if (Owner is not { } owner) return;
        Window dialog = Dialogs.Dialog(Loc.T("ADB einrichten", "Set up ADB"), 760, 520);
        var title = new TextBlock
        {
            Text = missing
                ? Loc.T("ADB fehlt – in wenigen Schritten startklar", "ADB is missing – let's get you started")
                : Loc.T("ADB und Android-Verbindung einrichten", "Set up ADB and your Android connection"),
            Margin = new Thickness(0, 0, 0, 14),
        };
        title.Classes.Add("section");
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.Muted,
            Text = Platform.IsWindows
                ? Loc.T(
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
                : Loc.T(
                    "1. Installiere ADB über die Paketverwaltung deiner Distribution, z. B. „sudo pacman -S android-tools“ (Arch/CachyOS), " +
                    "„sudo apt install adb“ (Debian/Ubuntu) oder „sudo dnf install android-tools“ (Fedora). ADBora findet es dann automatisch.\n\n" +
                    "2. Alternativ die SDK Platform-Tools für Linux von Google herunterladen, entpacken (z. B. nach ~/platform-tools) und unten ‚adb auswählen‘.\n\n" +
                    "3. Aktiviere auf Android die Entwickleroptionen (meist siebenmal auf die Build-Nummer tippen) und USB-Debugging.\n\n" +
                    "4. Verbinde das Gerät per USB-Datenkabel, entsperre es und bestätige den RSA-Dialog.\n\n" +
                    "5. Erscheint das Gerät als „no permissions“: die udev-Regeln für Android fehlen (Paket „android-udev“ bzw. „android-sdk-platform-tools-common“) " +
                    "oder der Benutzer ist nicht in der Gruppe „adbusers“/„plugdev“.",
                    "1. Install ADB with your distribution's package manager, e.g. \"sudo pacman -S android-tools\" (Arch/CachyOS), " +
                    "\"sudo apt install adb\" (Debian/Ubuntu) or \"sudo dnf install android-tools\" (Fedora). ADBora then finds it automatically.\n\n" +
                    "2. Alternatively download SDK Platform-Tools for Linux from Google, extract them (e.g. to ~/platform-tools) and click 'Choose adb' below.\n\n" +
                    "3. Enable Developer options on Android (usually tap the build number seven times), then enable USB debugging.\n\n" +
                    "4. Connect a USB data cable, unlock the device and accept the RSA prompt.\n\n" +
                    "5. If the device shows up as \"no permissions\": the Android udev rules are missing (package \"android-udev\" or " +
                    "\"android-sdk-platform-tools-common\") or the user is not in the group \"adbusers\"/\"plugdev\".")
        };
        string adbExe = Platform.Exe("adb");
        var download = Ui.Button("Platform-Tools herunterladen", "Download Platform-Tools", ButtonKind.Primary);
        var choose = Ui.Button($"{adbExe} auswählen …", $"Choose {adbExe} …");
        var close = Ui.Button("Schließen", "Close");
        close.IsCancel = true;
        bool chooseAfter = false;
        download.Click += (_, _) => Platform.Open(AdbLocator.PlatformToolsUrl + ("?hl=" + Loc.Language));
        choose.Click += (_, _) => { chooseAfter = true; dialog.Close(); };
        close.Click += (_, _) => dialog.Close();
        var buttons = Ui.Row(download, choose, close);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        var body = new DockPanel { Margin = new Thickness(22) };
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(title);
        body.Children.Add(buttons);
        body.Children.Add(new ScrollViewer { Content = text });
        dialog.Content = body;
        await dialog.ShowDialog(owner);
        if (chooseAfter) await ChooseAdb();
    }
}
