using System.Diagnostics;
using System.Text.RegularExpressions;
using AdbTool.Backup;
using AdbTool.Cast;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>
/// Tab "Stream": two modes. "Headset casting" shows the undistorted casting picture of
/// a Meta Quest or Play For Dream in an ADBora window (see AdbTool.Cast). "scrcpy" mirrors
/// the raw display of any Android device (separate scrcpy window) with view, crop,
/// recording, screenshot and audio/microphone forwarding.
/// </summary>
internal sealed class StreamPage : UserControl, IPage
{
    private static readonly (string De, string En, int Size, int Mbit)[] Qualities =
    {
        ("Niedrig · 1024 px · 4 Mbit/s", "Low · 1024 px · 4 Mbit/s", 1024, 4),
        ("Mittel · 1440 px · 8 Mbit/s", "Medium · 1440 px · 8 Mbit/s", 1440, 8),
        ("Hoch · 1920 px · 16 Mbit/s", "High · 1920 px · 16 Mbit/s", 1920, 16),
        ("Original · 32 Mbit/s", "Original · 32 Mbit/s", 0, 32),
    };
    private static readonly int[] FpsValues = { 30, 60, 72, 90 };

    private readonly AppState _state;
    private readonly Label _scrcpyStatus = Ui.Value("");
    private readonly DarkButton _download = Ui.Button("scrcpy herunterladen", "Download scrcpy", ButtonKind.Primary);
    private readonly DarkButton _choose = Ui.Button("scrcpy.exe auswählen …", "Choose scrcpy.exe …");

    private static readonly (string De, string En)[] Views =
    {
        ("Ganzes Bild", "Full picture"),
        ("Linke Hälfte (ein Auge)", "Left half (one eye)"),
        ("Oben links (Viertel)", "Top left (quarter)"),
        ("Unten links (Viertel)", "Bottom left (quarter)"),
    };
    private readonly DarkComboBox _view = Ui.Combo(260);
    private readonly DarkComboBox _rotation = Ui.Combo(120);
    private static readonly int[] ZoomValues = Enumerable.Range(0, 13).Select(i => 100 - i * 5).ToArray();       // 100 … 40 %
    private static readonly int[] OffsetValues = Enumerable.Range(-15, 31).Select(i => i * 2).ToArray();         // -30 … 30 %
    private static readonly double[] AngleValues = Enumerable.Range(-60, 121).Select(i => i * 0.5).ToArray();    // -30 … 30°
    private readonly DarkComboBox _zoom = Ui.Combo(100);
    private readonly DarkComboBox _offX = Ui.Combo(100);
    private readonly DarkComboBox _offY = Ui.Combo(100);
    private readonly DarkComboBox _angle = Ui.Combo(100);
    private readonly DarkButton _resetFine = Ui.Button("Zurücksetzen", "Reset");
    private string? _timeoutSerial;
    private string? _timeoutOld;
    private readonly Label _viewHint = Ui.Label("", "", muted: true);
    private readonly DarkComboBox _displayCombo = Ui.Combo(360);
    private readonly DarkButton _listDisplays = Ui.Button("Neu lesen", "Reload");
    private readonly List<(int Id, int W, int H, string Name)> _displays = new();
    private readonly DarkComboBox _quality = Ui.Combo(260);
    private readonly DarkComboBox _fps = Ui.Combo(120);
    private readonly DarkComboBox _audio = Ui.Combo(260);
    private readonly CheckBox _control = Ui.CheckBox("Mit Maus und Tastatur steuern", "Control with mouse and keyboard");
    private readonly CheckBox _onTop = Ui.CheckBox("Stream-Fenster immer im Vordergrund", "Stream window always on top");
    private readonly CheckBox _proximity = Ui.CheckBox("Näherungssensor während des Streams aus (Quest)", "Proximity sensor off while streaming (Quest)");
    private readonly CheckBox _record = Ui.CheckBox("Stream als Video aufnehmen (MP4)", "Record the stream as video (MP4)");
    private readonly TextBox _folder = Ui.TextBox(420);
    private readonly DarkButton _browse = Ui.Button("Ordner wählen …", "Choose folder …");
    private readonly TextBox _shotFolder = Ui.TextBox(420);
    private readonly DarkButton _shotBrowse = Ui.Button("Ordner wählen …", "Choose folder …");

    private readonly DarkButton _start = Ui.Button("Stream starten", "Start stream", ButtonKind.Primary);
    private readonly DarkButton _stop = Ui.Button("Stream beenden", "Stop stream", ButtonKind.Danger);
    private readonly DarkButton _screenshot = Ui.Button("Screenshot", "Screenshot");
    private readonly DarkButton _openFolder = Ui.Button("Videos öffnen", "Open videos");
    private readonly DarkButton _openShots = Ui.Button("Screenshots öffnen", "Open screenshots");
    private readonly DarkProgressBar _progress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 26, Margin = new Padding(8, 4, 0, 4), Visible = false, Maximum = 1000 };
    private readonly Label _status = Ui.Value("");
    private readonly TextBox _log = Ui.Console();

    // --- mode / headset casting
    private readonly RadioButton _modeCast = Ui.Radio("Headset-Casting (Meta Quest, Play For Dream)", "Headset casting (Meta Quest, Play For Dream)");
    private readonly RadioButton _modeScrcpy = Ui.Radio("scrcpy (alle Android-Geräte)", "scrcpy (all Android devices)");
    private readonly Label _modeHint = Ui.Label("", "", muted: true);
    private readonly RadioButton _hmdQuest = Ui.Radio("Meta Quest", "Meta Quest");
    private readonly RadioButton _hmdPfd = Ui.Radio("Play For Dream MR", "Play For Dream MR");
    private readonly Label _castInfo = Ui.Label("", "", muted: true);
    private readonly CheckBox _castProximity = Ui.CheckBox("Headset während des Streams wach halten", "Keep the headset awake while streaming");
    private readonly CheckBox _castAudio = Ui.CheckBox("Ton des Headsets am PC abspielen", "Play the headset audio on the PC");
    private static readonly int[] VolumeValues = Enumerable.Range(0, 21).Select(i => i * 10).ToArray(); // 0 … 200 %
    private readonly DarkComboBox _castVolume = Ui.Combo(100);
    private bool _volumeLoading;
    private readonly CheckBox _castMic = Ui.CheckBox("Mikrofon des Headsets einbeziehen", "Include the headset microphone");
    private readonly CheckBox _castOnTop = Ui.CheckBox("Stream-Fenster immer im Vordergrund", "Stream window always on top");
    private readonly CheckBox _castRecord = Ui.CheckBox("Stream mit Ton aufnehmen (MKV)", "Record the stream with audio (MKV)");
    private Card _optCard = null!, _castCard = null!;
    private Label _beta = null!, _toolHint = null!;
    private CastSession? _cast;
    private CastKind _detected = CastKind.None;
    private string? _detectedSerial;
    private bool _castStarting;

    private string? _scrcpy;
    private Process? _process;
    private string? _recordFile;
    private bool _proximityApplied;
    private string? _proximitySerial;
    private string? _displaySerial;
    private bool _downloading;

    public StreamPage(AppState state)
    {
        _state = state;
        BackColor = Theme.Background;
        DoubleBuffered = true;
        AutoScroll = true;
        Padding = new Padding(20, 16, 20, 16);
        BuildUi();

        _scrcpy = ScrcpyTool.Find(_state.Settings.ScrcpyPath);
        _ = ShowScrcpyAsync();

        _state.SelectedDeviceChanged += () => { _displaySerial = null; UpdateEnabled(); _ = DetectDisplayAsync(); _ = DetectCastAsync(); };
        _state.AdbChanged += UpdateEnabled;
        _state.BusyChanged += UpdateEnabled;
        Loc.LanguageChanged += () => { FillCombos(); ApplyMode(); _ = ShowScrcpyAsync(); };
        HandleDestroyed += (_, _) =>
        {
            StopStream(wait: false);
            _ = _cast?.StopAsync();
            // App is closing: restore the display timeout synchronously
            if (_timeoutOld is { } old && _timeoutSerial is not null && _state.HasAdb)
            {
                _timeoutOld = null;
                try
                {
                    using Process p = new AdbClient(_state.AdbPath!, _timeoutSerial).Start("shell", $"settings put system screen_off_timeout {old}");
                    p.WaitForExit(3000);
                }
                catch { }
            }
        };
        UpdateEnabled();
    }

    private void BuildUi()
    {
        // --- scrcpy -------------------------------------------------------------
        var toolCard = new Card("scrcpy", "scrcpy") { Dock = DockStyle.Top };
        var toolBox = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Dock = DockStyle.Top };
        toolBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var toolHint = _toolHint = Ui.Label(
            "Das Streaming nutzt scrcpy (Open Source, Apache-2.0); das Headset-Casting verwendet die darin enthaltenen FFmpeg-Bibliotheken zum Dekodieren. Es braucht keine App auf dem Headset und läuft über USB oder WLAN-ADB. scrcpy wird nicht mitgeliefert, sondern einmalig von GitHub geladen.",
            "Streaming uses scrcpy (open source, Apache-2.0); headset casting uses the FFmpeg libraries included with it for decoding. It needs no app on the headset and works via USB or Wi-Fi ADB. scrcpy is not bundled but downloaded once from GitHub.", muted: true);
        toolHint.Margin = new Padding(0, 0, 0, 6);
        Ui.WrapTo(toolHint, this, 40);
        Ui.WrapTo(_scrcpyStatus, this, 40);
        _download.Click += async (_, _) => await DownloadAsync();
        _choose.Click += (_, _) => ChooseScrcpy();
        var beta = _beta = Ui.Label(
            "scrcpy zeigt das rohe Display des Geräts. Bei VR-Headsets ist das Bild je nach Gerät verzerrt, schief oder als Raster – Ausschnitt, Drehung und Feinabstimmung helfen. Für Meta Quest und Play For Dream gibt es oben das entzerrte „Headset-Casting“.",
            "scrcpy shows the raw display of the device. On VR headsets the picture is distorted, tilted or a grid depending on the device – view, rotation and fine tuning help. For Meta Quest and Play For Dream use the undistorted \"Headset casting\" above.");
        beta.ForeColor = Theme.Warning;
        beta.Margin = new Padding(0, 0, 0, 8);
        Ui.WrapTo(beta, this, 40);
        toolBox.Controls.Add(beta);
        toolBox.Controls.Add(toolHint);
        toolBox.Controls.Add(_scrcpyStatus);
        var toolRow = Ui.Row(_download, _choose);
        toolRow.Margin = new Padding(0, 8, 0, 0);
        toolBox.Controls.Add(toolRow);
        toolCard.SetContent(toolBox);

        // --- options ------------------------------------------------------------
        var optCard = _optCard = new Card("scrcpy-Einstellungen", "scrcpy settings") { Dock = DockStyle.Top };
        var grid = Ui.Grid(2);
        FillCombos();
        _quality.SelectedIndex = Math.Clamp(_state.Settings.StreamQuality, 0, Qualities.Length - 1);
        _fps.SelectedIndex = Math.Clamp(_state.Settings.StreamFps, 0, FpsValues.Length - 1);
        _audio.SelectedIndex = Math.Clamp(_state.Settings.StreamAudio, 0, 2);
        _quality.SelectedIndexChanged += (_, _) => { _state.Settings.StreamQuality = _quality.SelectedIndex; _state.Settings.Save(); };
        _fps.SelectedIndexChanged += (_, _) => { _state.Settings.StreamFps = _fps.SelectedIndex; _state.Settings.Save(); };
        _audio.SelectedIndexChanged += (_, _) => { _state.Settings.StreamAudio = _audio.SelectedIndex; _state.Settings.Save(); };
        _view.SelectedIndex = 0;
        _view.SelectedIndexChanged += (_, _) => ShowViewHint();
        _displayCombo.SelectedIndexChanged += (_, _) => ShowViewHint();
        _viewHint.Margin = new Padding(0, 0, 0, 6);
        Ui.WrapTo(_viewHint, this, 200);

        _control.Checked = _state.Settings.StreamControl;
        _onTop.Checked = _state.Settings.StreamOnTop;
        _record.Checked = _state.Settings.StreamRecord;
        _proximity.Checked = true;
        _control.CheckedChanged += (_, _) => { _state.Settings.StreamControl = _control.Checked; _state.Settings.Save(); };
        _onTop.CheckedChanged += (_, _) => { _state.Settings.StreamOnTop = _onTop.Checked; _state.Settings.Save(); };
        _record.CheckedChanged += (_, _) => { _state.Settings.StreamRecord = _record.Checked; _state.Settings.Save(); UpdateEnabled(); };
        _folder.Text = _state.Settings.StreamFolder;
        _folder.TextChanged += (_, _) => { _state.Settings.StreamFolder = _folder.Text.Trim(); _state.Settings.Save(); };
        _browse.Click += (_, _) => ChooseFolder(_folder, Loc.T("Zielordner für Videoaufnahmen", "Folder for video recordings"));
        _shotFolder.Text = _state.Settings.ScreenshotFolder;
        _shotFolder.TextChanged += (_, _) => { _state.Settings.ScreenshotFolder = _shotFolder.Text.Trim(); _state.Settings.Save(); };
        _shotBrowse.Click += (_, _) => ChooseFolder(_shotFolder, Loc.T("Zielordner für Screenshots", "Folder for screenshots"));

        _displayCombo.Items.Add(Loc.T("Display 0 (Standard)", "Display 0 (default)"));
        _displayCombo.SelectedIndex = 0;
        _listDisplays.Click += async (_, _) => { _displaySerial = null; await DetectDisplayAsync(); };
        Ui.AddRow(grid, Ui.Label("Display", "Display", muted: true), Ui.Row(_displayCombo, _listDisplays));
        foreach (string r in new[] { "0°", "90°", "180°", "270°" }) _rotation.Items.Add(r);
        _rotation.SelectedIndex = 0;
        Ui.AddRow(grid, Ui.Label("Ausschnitt", "View", muted: true), _view);
        Ui.AddRow(grid, Ui.Label("Drehung", "Rotation", muted: true), _rotation);
        foreach (int z in ZoomValues) _zoom.Items.Add($"{z} %");
        foreach (int o in OffsetValues) { _offX.Items.Add($"{o:+0;-0;0} %"); _offY.Items.Add($"{o:+0;-0;0} %"); }
        foreach (double a in AngleValues) _angle.Items.Add($"{a:+0.0;-0.0;0.0}°");
        ResetFine();
        foreach (DarkComboBox c in new[] { _zoom, _offX, _offY, _angle })
            c.SelectedIndexChanged += (_, _) => ShowViewHint();
        _resetFine.Click += (_, _) => ResetFine();
        var fine = Ui.Row(Ui.Label("Grösse", "Size", muted: true), _zoom,
                          Ui.Label("Versatz X", "Offset X", muted: true), _offX,
                          Ui.Label("Y", "Y", muted: true), _offY,
                          Ui.Label("Neigung", "Tilt", muted: true), _angle, _resetFine);
        fine.WrapContents = true;
        Ui.AddRow(grid, Ui.Label("Feinabstimmung", "Fine tuning", muted: true), fine);
        Ui.AddRow(grid, new Label { Width = 1 }, _viewHint);
        Ui.AddRow(grid, Ui.Label("Qualität", "Quality", muted: true), _quality);
        Ui.AddRow(grid, Ui.Label("Bilder/s", "Frames/s", muted: true), _fps);
        Ui.AddRow(grid, Ui.Label("Ton", "Audio", muted: true), _audio);
        Ui.AddRow(grid, new Label { Width = 1 }, _control);
        Ui.AddRow(grid, new Label { Width = 1 }, _onTop);
        Ui.AddRow(grid, new Label { Width = 1 }, _proximity);
        Ui.AddRow(grid, new Label { Width = 1 }, _record);
        Ui.AddRow(grid, Ui.Label("Videos", "Videos", muted: true), Ui.Row(_folder, _browse));
        Ui.AddRow(grid, Ui.Label("Screenshots", "Screenshots", muted: true), Ui.Row(_shotFolder, _shotBrowse));
        optCard.SetContent(grid);

        // --- actions ------------------------------------------------------------
        _start.Click += async (_, _) => await StartStreamAsync();
        _stop.Click += (_, _) => StopStream(wait: true);
        _screenshot.Click += async (_, _) => await ScreenshotAsync();
        _openFolder.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(Folder); } catch { }
            SettingsPage.OpenPath(Folder);
        };
        _openShots.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(ShotFolder); } catch { }
            SettingsPage.OpenPath(ShotFolder);
        };
        var actions = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 6, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) };
        for (int i = 0; i < 5; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.Controls.Add(_start, 0, 0);
        actions.Controls.Add(_stop, 1, 0);
        actions.Controls.Add(_screenshot, 2, 0);
        actions.Controls.Add(_openFolder, 3, 0);
        actions.Controls.Add(_openShots, 4, 0);
        actions.Controls.Add(_progress, 5, 0);
        _status.AutoEllipsis = true;
        _status.Margin = new Padding(0, 6, 0, 6);
        actions.Controls.Add(_status, 0, 1);
        actions.SetColumnSpan(_status, 6);

        var logCard = new Card("Protokoll", "Log") { Dock = DockStyle.Top, Height = 200 };
        Ui.Placeholder(_log, "Hier erscheinen die Meldungen des Streams.", "Messages of the stream appear here.");
        logCard.SetContent(_log, fill: true);

        BuildCastUi();
        Controls.Add(logCard);
        Controls.Add(Spacer());
        Controls.Add(actions);
        Controls.Add(Spacer());
        Controls.Add(optCard);
        Controls.Add(_castCard);
        Controls.Add(Spacer());
        Controls.Add(toolCard);
        Controls.Add(Spacer());
        Controls.Add(BuildModeCard());
        ApplyMode();
    }

    private static Panel Spacer() => new() { Dock = DockStyle.Top, Height = 12, BackColor = Color.Transparent };

    private void FillCombos()
    {
        int q = _quality.SelectedIndex, f = _fps.SelectedIndex, a = _audio.SelectedIndex;
        _quality.Items.Clear();
        foreach (var x in Qualities) _quality.Items.Add(Loc.T(x.De, x.En));
        int v = _view.SelectedIndex;
        _view.Items.Clear();
        foreach (var x in Views) _view.Items.Add(Loc.T(x.De, x.En));
        if (v >= 0) _view.SelectedIndex = v;
        _fps.Items.Clear();
        foreach (int x in FpsValues) _fps.Items.Add($"{x}");
        _audio.Items.Clear();
        _audio.Items.Add(Loc.T("Kein Ton", "No audio"));
        _audio.Items.Add(Loc.T("Ton des Headsets", "Headset audio"));
        _audio.Items.Add(Loc.T("Mikrofon des Headsets", "Headset microphone"));
        if (q >= 0) _quality.SelectedIndex = q;
        if (f >= 0) _fps.SelectedIndex = f;
        if (a >= 0) _audio.SelectedIndex = a;
    }

    private string Folder => Environment.ExpandEnvironmentVariables(_folder.Text.Trim().Length > 0 ? _folder.Text.Trim() : _state.Settings.StreamFolder);
    private string ShotFolder => Environment.ExpandEnvironmentVariables(_shotFolder.Text.Trim().Length > 0 ? _shotFolder.Text.Trim()
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"));

    // ------------------------------------------------------------------

    public void OnActivated()
    {
        UpdateEnabled();
        _ = DetectDisplayAsync();
        _ = DetectCastAsync();
    }

    public void CancelOperation() => StopStream(wait: true);

    private bool Streaming => _process is { HasExited: false } || _cast is not null || _castStarting;

    private void UpdateEnabled()
    {
        if (InvokeRequired) { BeginInvoke(new Action(UpdateEnabled)); return; }
        bool ready = _state.HasAdb && _state.SelectedDevice is { IsReady: true };
        bool streaming = Streaming;
        _start.Enabled = ready && _scrcpy is not null && !streaming && !_downloading && (!_modeCast.Checked || SelectedCastKind != CastKind.None);
        _stop.Enabled = streaming;
        _screenshot.Enabled = ready && !_state.IsBusy;
        _download.Enabled = _choose.Enabled = !_downloading && !streaming;
        _listDisplays.Enabled = ready && !streaming;
        _displayCombo.Enabled = !streaming;
        foreach (Control c in new Control[] { _modeCast, _modeScrcpy, _hmdQuest, _hmdPfd, _castProximity, _castOnTop, _castRecord })
            c.Enabled = !streaming;
        foreach (Control c in new Control[] { _view, _rotation, _zoom, _offX, _offY, _angle, _resetFine, _displayCombo, _quality, _fps, _audio, _control, _onTop, _proximity, _record, _folder, _browse, _shotFolder, _shotBrowse })
            c.Enabled = !streaming;
        _proximity.Text = IsQuest
            ? Loc.T("Näherungssensor während des Streams aus", "Proximity sensor off while streaming")
            : Loc.T("Display-Timeout während des Streams auf 1 Stunde verlängern", "Extend the display timeout to 1 hour while streaming");

        if (!streaming && !_downloading)
        {
            _status.Text = !_state.HasAdb
                ? Loc.T("ADB ist nicht eingerichtet (Tab „Allgemeine Einstellungen“).", "ADB is not set up (\"General settings\" tab).")
                : _state.SelectedDevice is not { IsReady: true } d
                    ? Loc.T("Kein bereites Gerät ausgewählt.", "No ready device selected.")
                    : _scrcpy is null
                        ? Loc.T("scrcpy fehlt – oben „scrcpy herunterladen“ klicken.", "scrcpy is missing – click \"Download scrcpy\" above.")
                        : _modeCast.Checked && SelectedCastKind == CastKind.None
                            ? Loc.T("Headset auswählen (Meta Quest oder Play For Dream) oder zu „scrcpy“ wechseln.", "Choose the headset (Meta Quest or Play For Dream) or switch to \"scrcpy\".")
                            : Loc.T($"Bereit: {d.DisplayName} ({d.Serial})", $"Ready: {d.DisplayName} ({d.Serial})");
        }
    }

    private bool IsQuest => _state.SelectedDevice?.Model.Contains("Quest", StringComparison.OrdinalIgnoreCase) == true;

    private void Log(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => Log(text))); return; }
        Ui.AppendLine(_log, text);
    }

    // ------------------------------------------------------------------
    // scrcpy installation
    // ------------------------------------------------------------------

    private async Task ShowScrcpyAsync()
    {
        if (_scrcpy is null)
        {
            _scrcpyStatus.Text = Loc.T("scrcpy ist noch nicht vorhanden.", "scrcpy is not available yet.");
            _scrcpyStatus.ForeColor = Theme.Warning;
            Loc.Bind(_download, t => _download.Text = t, "scrcpy herunterladen", "Download scrcpy");
        }
        else
        {
            string exe = _scrcpy;
            string version = await ScrcpyTool.VersionAsync(exe);
            if (IsDisposed || exe != _scrcpy) return;
            _scrcpyStatus.Text = Loc.T($"scrcpy {version} · {exe}", $"scrcpy {version} · {exe}");
            _scrcpyStatus.ForeColor = Theme.Text;
            _download.Text = Loc.T("Neueste Version laden", "Get latest version");
        }
        UpdateEnabled();
    }

    private async Task DownloadAsync()
    {
        if (_downloading) return;
        _downloading = true;
        _progress.Visible = true;
        _progress.Value = 0;
        UpdateEnabled();
        _status.Text = Loc.T("scrcpy wird heruntergeladen …", "Downloading scrcpy …");
        var progress = new Progress<(long Done, long Total)>(p => _progress.Value = p.Total > 0 ? (int)(p.Done * 1000 / p.Total) : 0);
        try
        {
            string exe = await ScrcpyTool.DownloadLatestAsync(progress, Log, CancellationToken.None);
            _scrcpy = exe;
            _state.Settings.ScrcpyPath = exe;
            _state.Settings.Save();
            _status.Text = Loc.T("scrcpy ist bereit.", "scrcpy is ready.");
        }
        catch (Exception ex)
        {
            Log(Loc.T("Download fehlgeschlagen: ", "Download failed: ") + ex.Message);
            MessageBox.Show(FindForm(), Loc.T("scrcpy konnte nicht geladen werden:\n", "scrcpy could not be downloaded:\n") + ex.Message +
                Loc.T($"\n\nAlternativ manuell von {ScrcpyTool.ProjectUrl} laden und „scrcpy.exe auswählen …“ verwenden.",
                      $"\n\nAlternatively download it from {ScrcpyTool.ProjectUrl} and use \"Choose scrcpy.exe …\"."),
                "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _downloading = false;
            _progress.Visible = false;
            await ShowScrcpyAsync();
        }
    }

    private void ChooseScrcpy()
    {
        using var dialog = new OpenFileDialog { Title = Loc.T("scrcpy.exe auswählen", "Choose scrcpy.exe"), Filter = "scrcpy.exe|scrcpy.exe" };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        _scrcpy = dialog.FileName;
        _state.Settings.ScrcpyPath = _scrcpy;
        _state.Settings.Save();
        _ = ShowScrcpyAsync();
    }

    private void ChooseFolder(TextBox box, string title)
    {
        using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
        string current = Environment.ExpandEnvironmentVariables(box.Text.Trim());
        if (Directory.Exists(current)) dialog.SelectedPath = current;
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) box.Text = dialog.SelectedPath;
    }

    // ------------------------------------------------------------------
    // Display / view
    // ------------------------------------------------------------------

    private (int Id, int W, int H, string Name)? SelectedDisplay =>
        _displayCombo.SelectedIndex >= 0 && _displayCombo.SelectedIndex < _displays.Count ? _displays[_displayCombo.SelectedIndex] : null;

    /// <summary>
    /// Reads the displays of the device ("dumpsys display"). XR headsets often render
    /// into additional displays: the Play For Dream / YVR shows the headset view in the
    /// virtual display "com.yvr.spatialhome" (2 × 2 layout); a Meta Quest uses display 0
    /// with both eyes side by side. A matching display and view are preselected.
    /// </summary>
    private async Task DetectDisplayAsync()
    {
        AdbDevice? device = _state.SelectedDevice;
        if (device is not { IsReady: true } || !_state.HasAdb) { ShowViewHint(); return; }
        if (_displaySerial == device.Serial && _displays.Count > 0) { ShowViewHint(); return; }
        var found = new List<(int Id, int W, int H, string Name)>();
        try
        {
            AdbResult r = await new AdbClient(_state.AdbPath!, device.Serial).CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(15), "shell", "dumpsys display");
            foreach (Match m in Regex.Matches(r.Output, @"mBaseDisplayInfo=DisplayInfo\{""([^""]*)"", displayId (\d+).*?, real (\d+) x (\d+)"))
            {
                int id = int.Parse(m.Groups[2].Value), w = int.Parse(m.Groups[3].Value), h = int.Parse(m.Groups[4].Value);
                if (w < 200 || h < 200 || found.Any(f => f.Id == id)) continue; // overlays (16 × 16) etc.
                found.Add((id, w, h, m.Groups[1].Value));
            }
            if (found.Count == 0)
            {
                AdbResult size = await new AdbClient(_state.AdbPath!, device.Serial).CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "shell", "wm size");
                Match m = Regex.Match(size.Output, @"Physical size:\s*(\d+)x(\d+)");
                if (m.Success) found.Add((0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), "Built-in Screen"));
            }
        }
        catch { }
        if (_state.SelectedDevice?.Serial != device.Serial || IsDisposed) return;

        _displays.Clear();
        _displays.AddRange(found.OrderBy(f => f.Id));
        _displaySerial = device.Serial;
        _displayCombo.Items.Clear();
        foreach (var d in _displays)
            _displayCombo.Items.Add($"Display {d.Id} · {d.W} × {d.H} · {d.Name}");
        if (_displays.Count == 0)
        {
            _displays.Add((0, 0, 0, ""));
            _displayCombo.Items.Add(Loc.T("Display 0 (Standard)", "Display 0 (default)"));
        }

        // Preselection: last settings for this model, otherwise automatic
        if (_state.Settings.StreamPresets.TryGetValue(device.Model, out StreamPreset? preset))
        {
            int idx = _displays.FindIndex(d => d.Name == preset.Display);
            _displayCombo.SelectedIndex = Math.Max(0, idx);
            _view.SelectedIndex = Math.Clamp(preset.View, 0, Views.Length - 1);
            _rotation.SelectedIndex = Math.Clamp(preset.Rotation, 0, 3);
            SelectValue(_zoom, ZoomValues, preset.Zoom);
            SelectValue(_offX, OffsetValues, preset.OffsetX);
            SelectValue(_offY, OffsetValues, preset.OffsetY);
            _angle.SelectedIndex = Math.Max(0, Array.FindIndex(AngleValues, a => Math.Abs(a - preset.Angle) < 0.01));
            ShowViewHint();
            return;
        }
        ResetFine();
        int yvr = _displays.FindIndex(d => d.Name.Contains("spatialhome", StringComparison.OrdinalIgnoreCase));
        if (yvr >= 0)
        {
            _displayCombo.SelectedIndex = yvr;
            _view.SelectedIndex = 2;     // top left = one eye, undistorted
            _rotation.SelectedIndex = 3; // the eye images are stored rotated by 90°
        }
        else
        {
            _displayCombo.SelectedIndex = Math.Max(0, _displays.FindIndex(d => d.Id == 0));
            var d0 = _displays[_displayCombo.SelectedIndex];
            _view.SelectedIndex = d0.W > d0.H * 1.6 ? 1 : 0; // both eyes side by side → one eye
            _rotation.SelectedIndex = 0;
            if (device.Model.Contains("Quest_3", StringComparison.OrdinalIgnoreCase) || device.Model.Contains("Quest 3", StringComparison.OrdinalIgnoreCase))
            {
                // The Quest 3 displays are mounted tilted: straighten the image and cut off the black corners
                _angle.SelectedIndex = Math.Max(0, Array.FindIndex(AngleValues, a => a == 20));
                SelectValue(_zoom, ZoomValues, 75);
            }
        }
        ShowViewHint();
    }

    private void ShowViewHint()
    {
        if (IsDisposed) return;
        string hint = "";
        if (SelectedDisplay is { W: > 0 } d)
        {
            hint = d.Name.Contains("spatialhome", StringComparison.OrdinalIgnoreCase)
                ? Loc.T("Play For Dream: Dieses Display zeigt die Headset-Ansicht als 2 × 2-Raster – oben die entzerrten Augen, unten die Linsenansicht. „Oben links“ zeigt ein normales Bild.",
                        "Play For Dream: this display shows the headset view as a 2 × 2 grid – undistorted eyes at the top, lens view at the bottom. \"Top left\" shows a normal picture.")
                : d.W > d.H * 1.6
                    ? Loc.T("Beide Augen nebeneinander – „Linke Hälfte“ zeigt ein normales Bild.", "Both eyes side by side – \"Left half\" shows a normal picture.")
                    : "";
            if (Crop(d) is { } c) hint += (hint.Length > 0 ? "  " : "") + Loc.T($"Ausschnitt: {c}", $"Crop: {c}");
        }
        _viewHint.Text = hint;
        UpdateEnabled();
    }

    private static void SelectValue(DarkComboBox combo, int[] values, int value) =>
        combo.SelectedIndex = Math.Max(0, Array.IndexOf(values, value));

    private int ZoomPercent => ZoomValues[Math.Max(0, _zoom.SelectedIndex)];
    private int OffsetX => OffsetValues[Math.Max(0, _offX.SelectedIndex)];
    private int OffsetY => OffsetValues[Math.Max(0, _offY.SelectedIndex)];
    private double Angle => AngleValues[Math.Max(0, _angle.SelectedIndex)];

    private void ResetFine()
    {
        SelectValue(_zoom, ZoomValues, 100);
        SelectValue(_offX, OffsetValues, 0);
        SelectValue(_offY, OffsetValues, 0);
        _angle.SelectedIndex = Array.FindIndex(AngleValues, a => a == 0);
    }

    /// <summary>
    /// scrcpy crop "w:h:x:y": region of the selected view (full, half, quarter),
    /// reduced to "size" percent around its centre and shifted by the offsets.
    /// Null = full picture without crop.
    /// </summary>
    private string? Crop((int Id, int W, int H, string Name) d)
    {
        if (d.W <= 0) return null;
        (int x, int y, int w, int h) = _view.SelectedIndex switch
        {
            1 => (0, 0, d.W / 2, d.H),
            2 => (0, 0, d.W / 2, d.H / 2),
            3 => (0, d.H / 2, d.W / 2, d.H / 2),
            _ => (0, 0, d.W, d.H)
        };
        int zoom = ZoomPercent;
        if (_view.SelectedIndex == 0 && zoom == 100 && OffsetX == 0 && OffsetY == 0)
            return null;
        int cw = w * zoom / 100 / 2 * 2, ch = h * zoom / 100 / 2 * 2; // even sizes for the encoder
        int cx = x + (w - cw) / 2 + w * OffsetX / 100;
        int cy = y + (h - ch) / 2 + h * OffsetY / 100;
        cx = Math.Clamp(cx, x, x + w - cw);
        cy = Math.Clamp(cy, y, y + h - ch);
        return $"{cw}:{ch}:{cx}:{cy}";
    }

    // ------------------------------------------------------------------
    // Stream
    // ------------------------------------------------------------------

    private async Task StartStreamAsync()
    {
        if (_modeCast.Checked) { await StartCastAsync(); return; }
        AdbDevice? device = _state.SelectedDevice;
        if (_scrcpy is null || device is not { IsReady: true } || Streaming) return;
        await DetectDisplayAsync();

        var args = new List<string> { $"--serial={device.Serial}", $"--window-title=ADBora Stream – {device.DisplayName}" };
        var display = SelectedDisplay ?? (0, 0, 0, "");
        if (display.Id != 0)
            args.Add($"--display-id={display.Id}");
        var quality = Qualities[Math.Max(0, _quality.SelectedIndex)];
        if (quality.Size > 0) args.Add($"--max-size={quality.Size}");
        args.Add($"--video-bit-rate={quality.Mbit}M");
        args.Add($"--max-fps={FpsValues[Math.Max(0, _fps.SelectedIndex)]}");
        if (Crop(display) is { } crop)
            args.Add($"--crop={crop}");
        if (_rotation.SelectedIndex > 0)
            args.Add($"--orientation={_rotation.SelectedIndex * 90}"); // window and recording
        if (Angle != 0)
            args.Add(FormattableString.Invariant($"--angle={Angle:0.0}"));

        // Remember these view settings for this device model
        _state.Settings.StreamPresets[device.Model] = new StreamPreset
        {
            Display = display.Name, View = _view.SelectedIndex, Rotation = _rotation.SelectedIndex,
            Zoom = ZoomPercent, OffsetX = OffsetX, OffsetY = OffsetY, Angle = Angle
        };
        _state.Settings.Save();
        switch (_audio.SelectedIndex)
        {
            case 1: args.Add("--audio-source=output"); break;
            case 2: args.Add("--audio-source=mic"); break;
            default: args.Add("--no-audio"); break;
        }
        if (!_control.Checked) args.Add("--no-control");
        if (_onTop.Checked) args.Add("--always-on-top");

        _recordFile = null;
        if (_record.Checked)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                _recordFile = Path.Combine(Folder, $"ADBora_{BackupService.SafeName(device.DisplayName, 40)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
                args.Add($"--record={_recordFile}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), Loc.T("Zielordner ungültig: ", "Invalid folder: ") + ex.Message, "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        if (_proximity.Checked) await KeepAwakeAsync(device);

        var psi = new ProcessStartInfo(_scrcpy)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_scrcpy)!
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        // Use ADBora's adb (avoids two different adb servers fighting over the device)
        psi.Environment["ADB"] = _state.AdbPath!;

        Log("");
        Log("> scrcpy " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        try
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) Log(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) Log(e.Data); };
            p.Exited += (_, _) => BeginInvoke(new Action(() => OnStreamExited(p)));
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _process = p;
            _status.Text = Loc.T($"Stream läuft: {device.DisplayName}", $"Streaming: {device.DisplayName}") +
                           (_recordFile is not null ? Loc.T(" · Aufnahme läuft", " · recording") : "");
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            await RestoreProximityAsync();
        }
        UpdateEnabled();
    }

    /// <summary>Quest: proximity sensor off; other devices: display timeout 1 hour (both restored afterwards).</summary>
    private async Task KeepAwakeAsync(AdbDevice device)
    {
        var adb = new AdbClient(_state.AdbPath!, device.Serial);
        if (IsQuest)
        {
            await adb.CaptureAsync(CancellationToken.None, "shell", "am broadcast -a com.oculus.vrpowermanager.prox_close");
            _proximityApplied = true;
            _proximitySerial = device.Serial;
        }
        else
        {
            // Other headsets: keep the display on longer (restored after the stream)
            AdbResult old = await adb.CaptureAsync(CancellationToken.None, "shell", "settings get system screen_off_timeout");
            string value = old.Output.Trim();
            if (old.Ok && Regex.IsMatch(value, @"^\d+$") && value != "3600000")
            {
                _timeoutOld = value;
                _timeoutSerial = device.Serial;
                await adb.CaptureAsync(CancellationToken.None, "shell", "settings put system screen_off_timeout 3600000");
                Log(Loc.T($"Display-Timeout auf 1 Stunde gesetzt (vorher {int.Parse(value) / 1000} s).", $"Display timeout set to 1 hour (before {int.Parse(value) / 1000} s)."));
            }
        }
    }

    private async void OnStreamExited(Process p)
    {
        if (!ReferenceEquals(p, _process)) return;
        int code = -1;
        try { code = p.ExitCode; } catch { }
        _process = null;
        p.Dispose();
        Log(Loc.T($"Stream beendet (Code {code}).", $"Stream ended (code {code})."));
        if (_recordFile is not null && File.Exists(_recordFile))
            Log(Loc.T($"Aufnahme gespeichert: {_recordFile}", $"Recording saved: {_recordFile}"));
        await RestoreProximityAsync();
        UpdateEnabled();
        _status.Text = Loc.T("Stream beendet.", "Stream ended.");
    }

    private async Task RestoreProximityAsync()
    {
        if (_timeoutOld is not null && _timeoutSerial is not null && _state.HasAdb)
        {
            string old = _timeoutOld;
            _timeoutOld = null;
            try
            {
                await new AdbClient(_state.AdbPath!, _timeoutSerial)
                    .CaptureAsync(CancellationToken.None, "shell", $"settings put system screen_off_timeout {old}");
                Log(Loc.T($"Display-Timeout wiederhergestellt ({int.Parse(old) / 1000} s).", $"Display timeout restored ({int.Parse(old) / 1000} s)."));
            }
            catch { }
        }
        if (!_proximityApplied || _proximitySerial is null || !_state.HasAdb) return;
        _proximityApplied = false;
        try
        {
            await new AdbClient(_state.AdbPath!, _proximitySerial)
                .CaptureAsync(CancellationToken.None, "shell", "am broadcast -a com.oculus.vrpowermanager.automation_disable");
        }
        catch { }
    }

    /// <summary>Closes the scrcpy window (so a recording is finalized properly), kills it if needed.</summary>
    private void StopStream(bool wait)
    {
        if (_cast is not null) { _ = StopCastAsync(); return; }
        Process? p = _process;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                p.Refresh();
                bool closed = p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow();
                if (!closed || !p.WaitForExit(wait ? 6000 : 1500))
                    AdbClient.KillQuietly(p);
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // Mode switch and headset casting
    // ------------------------------------------------------------------

    private Control BuildModeCard()
    {
        var card = new Card("Modus", "Mode") { Dock = DockStyle.Top };
        var box = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Dock = DockStyle.Top };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = Ui.Row(_modeCast, _modeScrcpy);
        row.WrapContents = true;
        box.Controls.Add(row);
        _modeHint.Margin = new Padding(0, 4, 0, 0);
        Ui.WrapTo(_modeHint, this, 40);
        box.Controls.Add(_modeHint);
        card.SetContent(box);
        _modeCast.Checked = true;
        _modeCast.CheckedChanged += (_, _) => { if (_modeCast.Checked) ApplyMode(); };
        _modeScrcpy.CheckedChanged += (_, _) => { if (_modeScrcpy.Checked) ApplyMode(); };
        return card;
    }

    private void BuildCastUi()
    {
        _castCard = new Card("Headset-Casting", "Headset casting") { Dock = DockStyle.Top };
        var grid = Ui.Grid(2);
        var hmds = Ui.Row(_hmdQuest, _hmdPfd);
        hmds.WrapContents = true;
        Ui.AddRow(grid, Ui.Label("Headset", "Headset", muted: true), hmds);
        _castInfo.Margin = new Padding(0, 2, 0, 8);
        Ui.WrapTo(_castInfo, this, 200);
        Ui.AddRow(grid, new Label { Width = 1 }, _castInfo);
        _castProximity.Checked = true;
        _castOnTop.Checked = _state.Settings.StreamOnTop;
        _castRecord.Checked = _state.Settings.StreamRecord;
        _castOnTop.CheckedChanged += (_, _) => { _state.Settings.StreamOnTop = _castOnTop.Checked; _onTop.Checked = _castOnTop.Checked; _state.Settings.Save(); };
        _castRecord.CheckedChanged += (_, _) => { _state.Settings.StreamRecord = _castRecord.Checked; _record.Checked = _castRecord.Checked; _state.Settings.Save(); };
        _castAudio.Checked = _state.Settings.CastAudio;
        _castAudio.CheckedChanged += (_, _) =>
        {
            _state.Settings.CastAudio = _castAudio.Checked;
            _state.Settings.Save();
            if (_cast is not null) _cast.Muted = !_castAudio.Checked;
        };
        foreach (int v in VolumeValues) _castVolume.Items.Add($"{v} %");
        _castVolume.SelectedIndexChanged += (_, _) =>
        {
            if (_volumeLoading || _castVolume.SelectedIndex < 0) return;
            int v = VolumeValues[_castVolume.SelectedIndex];
            if (SelectedCastKind == CastKind.PlayForDream) _state.Settings.CastVolumePfd = v; else _state.Settings.CastVolumeQuest = v;
            _state.Settings.Save();
            if (_cast is not null) _cast.Volume = v;
        };
        LoadVolume();
        var audioRow = Ui.Row(_castAudio, Ui.Label("Lautstärke", "Volume", muted: true), _castVolume);
        audioRow.WrapContents = true;
        Ui.AddRow(grid, new Label { Width = 1 }, audioRow);
        _castMic.Checked = _state.Settings.CastMic;
        _castMic.CheckedChanged += async (_, _) =>
        {
            _state.Settings.CastMic = _castMic.Checked;
            _state.Settings.Save();
            if (_cast is not null) await ApplyMicAsync(_cast);
        };
        var micHint = Ui.Label("Dafür installiert ADBora einmalig die kleine Begleit-App „ADBora Mic“ (Open Source, ohne Netzwerkzugriff) auf dem Headset; das Casting der Hersteller schickt das Mikrofon nicht mit.",
                               "For this ADBora installs the small companion app \"ADBora Mic\" (open source, no network access) on the headset once; the manufacturers' casting does not send the microphone.", muted: true);
        Ui.WrapTo(micHint, this, 200);
        Ui.AddRow(grid, new Label { Width = 1 }, _castMic);
        Ui.AddRow(grid, new Label { Width = 1 }, micHint);
        Ui.AddRow(grid, new Label { Width = 1 }, _castProximity);
        Ui.AddRow(grid, new Label { Width = 1 }, _castOnTop);
        Ui.AddRow(grid, new Label { Width = 1 }, _castRecord);
        var keys = Ui.Label("Im Stream-Fenster: Doppelklick oder F11 = Vollbild, Esc = Vollbild beenden, F12 = Screenshot, M = Ton ein/aus, + / − = Lautstärke.",
                            "In the stream window: double-click or F11 = full screen, Esc = leave full screen, F12 = screenshot, M = audio on/off, + / − = volume.", muted: true);
        Ui.WrapTo(keys, this, 200);
        Ui.AddRow(grid, new Label { Width = 1 }, keys);
        _castCard.SetContent(grid);
        _hmdQuest.CheckedChanged += (_, _) => { LoadVolume(); ShowCastInfo(); };
        _hmdPfd.CheckedChanged += (_, _) => { LoadVolume(); ShowCastInfo(); };
    }

    /// <summary>Volume of the selected headset (saved separately for Quest and PFD).</summary>
    private void LoadVolume()
    {
        int v = SelectedCastKind == CastKind.PlayForDream ? _state.Settings.CastVolumePfd : _state.Settings.CastVolumeQuest;
        _volumeLoading = true;
        _castVolume.SelectedIndex = Math.Max(0, Array.IndexOf(VolumeValues, Math.Clamp(v / 10 * 10, 0, 200)));
        _volumeLoading = false;
    }

    private CastKind SelectedCastKind => _hmdQuest.Checked ? CastKind.Quest : _hmdPfd.Checked ? CastKind.PlayForDream : CastKind.None;

    private void ApplyMode()
    {
        if (_castCard is null) return;
        bool cast = _modeCast.Checked;
        _castCard.Visible = cast;
        _optCard.Visible = !cast;
        _beta.Visible = !cast;
        _modeHint.Text = cast
            ? Loc.T("Entzerrtes Bild wie beim offiziellen Casting, direkt über das USB-Kabel (oder WLAN-ADB) – ohne Browser, Handy-App oder Meta-Konto.",
                    "Undistorted picture like official casting, directly over the USB cable (or Wi-Fi ADB) – no browser, phone app or Meta account.")
            : Loc.T("Spiegelt das rohe Display beliebiger Android-Geräte mit scrcpy (eigenes Fenster, Steuerung mit Maus/Tastatur, Ton, MP4-Aufnahme).",
                    "Mirrors the raw display of any Android device with scrcpy (own window, mouse/keyboard control, audio, MP4 recording).");
        ShowCastInfo();
    }

    private void ShowCastInfo()
    {
        if (IsDisposed) return;
        string auto = _detected != CastKind.None && SelectedCastKind == _detected ? Loc.T(" (automatisch erkannt)", " (detected automatically)") : "";
        _castInfo.Text = SelectedCastKind switch
        {
            CastKind.Quest => Loc.T("Meta Quest: nutzt den Casting-Dienst der Quest (wie der Meta Quest Developer Hub), ca. 2064 × 1440 bei 30 fps. Ein laufendes Casting zu Handy, Browser oder SideQuest vorher beenden.",
                                    "Meta Quest: uses the Quest's casting service (like the Meta Quest Developer Hub), about 2064 × 1440 at 30 fps. Stop any casting to phone, browser or SideQuest first.") + auto,
            CastKind.PlayForDream => Loc.T("Play For Dream: nutzt den eingebauten Casting-Dienst der PFD (Einzelauge, 1280 × 720 bei ca. 24 fps). Miracast zum TV vorher beenden.",
                                           "Play For Dream: uses the built-in PFD casting service (single eye, 1280 × 720 at about 24 fps). Stop Miracast to a TV first.") + auto,
            _ => Loc.T("Für andere Geräte „scrcpy“ verwenden.", "Use \"scrcpy\" for other devices.")
        };
        UpdateEnabled();
    }

    /// <summary>Selects headset and mode automatically for the selected device.</summary>
    private async Task DetectCastAsync()
    {
        AdbDevice? device = _state.SelectedDevice;
        if (device is not { IsReady: true } || !_state.HasAdb || Streaming) return;
        if (_detectedSerial == device.Serial) return;
        CastKind kind = await CastSource.DetectAsync(_state.AdbPath!, device);
        if (IsDisposed || _state.SelectedDevice?.Serial != device.Serial || Streaming) return;
        _detectedSerial = device.Serial;
        _detected = kind;
        if (kind == CastKind.Quest) _hmdQuest.Checked = true;
        else if (kind == CastKind.PlayForDream) _hmdPfd.Checked = true;
        if (kind != CastKind.None) _modeCast.Checked = true; else _modeScrcpy.Checked = true;
        ApplyMode();
    }

    private async Task StartCastAsync()
    {
        AdbDevice? device = _state.SelectedDevice;
        CastKind kind = SelectedCastKind;
        if (_scrcpy is null || device is not { IsReady: true } || Streaming || kind == CastKind.None) return;
        _castStarting = true;
        UpdateEnabled();
        string? record = null;
        if (_castRecord.Checked)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                record = Path.Combine(Folder, $"ADBora_{BackupService.SafeName(device.DisplayName, 40)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.dat");
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), Loc.T("Zielordner ungültig: ", "Invalid folder: ") + ex.Message, "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _castStarting = false;
                UpdateEnabled();
                return;
            }
        }
        if (_castProximity.Checked) await KeepAwakeAsync(device);

        var session = new CastSession(kind, _state.AdbPath!, device.Serial, Path.GetDirectoryName(_scrcpy)!);
        session.Log += Log;
        session.ScreenshotRequested += () => BeginInvoke(new Action(() => _ = ScreenshotAsync()));
        session.MuteToggleRequested += () => BeginInvoke(new Action(() => _castAudio.Checked = !_castAudio.Checked));
        session.VolumeStepRequested += d => BeginInvoke(new Action(() =>
            _castVolume.SelectedIndex = Math.Clamp(_castVolume.SelectedIndex + d, 0, VolumeValues.Length - 1)));
        session.Volume = VolumeValues[Math.Max(0, _castVolume.SelectedIndex)];
        session.Ended += reason => BeginInvoke(new Action(() => OnCastEnded(session, reason)));
        _cast = session;
        Log("");
        Log(Loc.T($"Headset-Casting: {device.DisplayName} ({device.Serial})", $"Headset casting: {device.DisplayName} ({device.Serial})"));
        try
        {
            await session.StartAsync($"ADBora Stream – {device.DisplayName}", _castOnTop.Checked, record, FindForm(), _castAudio.Checked);
            if (_cast == session)
            {
                if (_castMic.Checked) await ApplyMicAsync(session);
                _status.Text = Loc.T($"Stream läuft: {device.DisplayName}", $"Streaming: {device.DisplayName}") +
                               (session.RecordFile is not null ? Loc.T(" · Aufnahme läuft", " · recording") : "");
                Log(Loc.T("Bild wird empfangen.", "Receiving picture."));
            }
        }
        catch (Exception ex)
        {
            Log(Loc.T("Casting fehlgeschlagen: ", "Casting failed: ") + ex.Message);
            if (_cast == session) _cast = null;
            await RestoreProximityAsync();
            if (!IsDisposed)
                MessageBox.Show(FindForm(), ex.Message, "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _castStarting = false;
            UpdateEnabled();
        }
    }

    /// <summary>Switches the headset microphone on or off according to the check box.</summary>
    private async Task ApplyMicAsync(CastSession session)
    {
        try { await session.SetMicAsync(_castMic.Checked); }
        catch (Exception ex)
        {
            Log(Loc.T("Mikrofon nicht verfügbar: ", "Microphone not available: ") + ex.Message);
        }
    }

    private async Task StopCastAsync()
    {
        CastSession? s = _cast;
        if (s is null) return;
        await s.StopAsync();
    }

    private async void OnCastEnded(CastSession session, string? reason)
    {
        if (_cast != session) return;
        _cast = null;
        await session.StopAsync();
        Log(reason ?? Loc.T("Stream beendet.", "Stream ended."));
        if (session.RecordFile is { } f && File.Exists(f))
            Log(Loc.T($"Aufnahme gespeichert: {f}", $"Recording saved: {f}"));
        await RestoreProximityAsync();
        _status.Text = reason ?? Loc.T("Stream beendet.", "Stream ended.");
        UpdateEnabled();
    }

    // ------------------------------------------------------------------
    // Screenshot
    // ------------------------------------------------------------------

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// While streaming: saves exactly what the stream window shows (all views and displays).
    /// Without a stream: "screencap" of display 0 (with the chosen crop).
    /// </summary>
    private async Task ScreenshotAsync()
    {
        AdbDevice? device = _state.SelectedDevice;
        if (device is not { IsReady: true } || !_state.HasAdb) return;
        _screenshot.Enabled = false;
        try
        {
            Directory.CreateDirectory(ShotFolder);
            string file = Path.Combine(ShotFolder, $"ADBora_{BackupService.SafeName(device.DisplayName, 40)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");

            if (_cast is not null)
            {
                using Bitmap shot = _cast.Snapshot() ?? throw new InvalidOperationException(Loc.T("Noch kein Bild vorhanden.", "No picture yet."));
                shot.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
            else if (_process is { HasExited: false } p)
            {
                p.Refresh();
                IntPtr hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero) throw new InvalidOperationException(Loc.T("Stream-Fenster nicht gefunden.", "Stream window not found."));
                SetForegroundWindow(hwnd);
                await Task.Delay(250);
                GetClientRect(hwnd, out RECT rc);
                var origin = new Point(0, 0);
                ClientToScreen(hwnd, ref origin);
                int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
                if (w <= 0 || h <= 0) throw new InvalidOperationException(Loc.T("Stream-Fenster ist minimiert.", "Stream window is minimized."));
                using var bmp = new Bitmap(w, h);
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(origin, Point.Empty, new Size(w, h));
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                FindForm()?.Activate();
            }
            else
            {
                if (SelectedDisplay is { Id: not 0 })
                {
                    MessageBox.Show(FindForm(), Loc.T("Für dieses Display ist ein Screenshot nur während eines laufenden Streams möglich. Bitte zuerst „Stream starten“.",
                            "For this display a screenshot is only possible while a stream is running. Please \"Start stream\" first."),
                        "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                await new AdbClient(_state.AdbPath!, device.Serial).ExecOutToFileAsync("screencap -p", file, CancellationToken.None);
                if (SelectedDisplay is { } d && Crop(d) is { } crop)
                {
                    int[] c = crop.Split(':').Select(int.Parse).ToArray();
                    string temp = file + ".tmp";
                    using (var full = new Bitmap(file))
                    {
                        var rect = Rectangle.Intersect(new Rectangle(c[2], c[3], c[0], c[1]), new Rectangle(0, 0, full.Width, full.Height));
                        if (rect.Width > 0 && rect.Height > 0)
                        {
                            using Bitmap part = full.Clone(rect, full.PixelFormat);
                            part.Save(temp, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    if (File.Exists(temp)) File.Move(temp, file, overwrite: true);
                }
            }
            Log(Loc.T($"Screenshot gespeichert: {file}", $"Screenshot saved: {file}"));
            _status.Text = Loc.T($"Screenshot gespeichert: {Path.GetFileName(file)}", $"Screenshot saved: {Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            Log(Loc.T("Screenshot fehlgeschlagen: ", "Screenshot failed: ") + ex.Message);
        }
        finally
        {
            UpdateEnabled();
        }
    }
}
