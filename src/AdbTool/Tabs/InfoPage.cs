using System.Runtime.InteropServices;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>Tab 6: build information, licence notes and update check.</summary>
internal sealed class InfoPage : UserControl, IPage
{
    private readonly AppState _state;
    private readonly Action _exitApp;
    private readonly Label _version = Ui.Value();
    private readonly Label _build = Ui.Value();
    private readonly Label _variant = Ui.Value();
    private readonly Label _dotnet = Ui.Value();
    private readonly Label _os = Ui.Value();
    private readonly Label _adbPath = Ui.Value();
    private readonly Label _adbVersion = Ui.Value();
    private readonly LinkLabel _repo = Link(Updater.RepoUrl);
    private readonly LinkLabel _data = Link(AppSettings.DataDirectory);
    private readonly Label _updateStatus = Ui.Value("");
    private readonly CheckBox _checkOnStart = Ui.CheckBox("Beim Start automatisch nach Updates suchen", "Check for updates automatically at start");
    private readonly DarkButton _checkNow = Ui.Button("Nach Updates suchen", "Check for updates", ButtonKind.Primary);
    private readonly DarkButton _copy = Ui.Button("Version kopieren", "Copy version");
    private readonly DarkButton _github = Ui.Button("GitHub öffnen", "Open GitHub");
    private readonly DarkButton _releases = Ui.Button("Alle Versionen", "All releases");

    public InfoPage(AppState state, Action exitApp)
    {
        _state = state;
        _exitApp = exitApp;
        BackColor = Theme.Background;
        DoubleBuffered = true;
        AutoScroll = true;
        Padding = new Padding(20, 16, 20, 16);

        BuildUi();
        Reload(false);

        UpdateUi.StatusChanged += () =>
        {
            if (IsDisposed) return;
            if (InvokeRequired) { if (IsHandleCreated) BeginInvoke(new Action(ShowUpdateStatus)); }
            else ShowUpdateStatus();
        };
        Loc.LanguageChanged += () => Reload(false);
        _state.AdbChanged += () => Reload(false);
    }

    private static LinkLabel Link(string text)
    {
        var link = new LinkLabel
        {
            AutoSize = true,
            Text = text,
            Font = Theme.BaseFont,
            BackColor = Color.Transparent,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.Text,
            VisitedLinkColor = Theme.Accent,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, 8, 0, 4),
            UseMnemonic = false
        };
        return link;
    }

    private void BuildUi()
    {
        // --- Build info -----------------------------------------------------
        var infoCard = new Card { Dock = DockStyle.Top };
        var infoBox = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Dock = DockStyle.Top };
        infoBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var brand = new BrandLabel { Margin = new Padding(0, 0, 0, 0) };
        var subtitle = Ui.Label("Android Device Toolbox", "Android Device Toolbox", muted: true);
        subtitle.Margin = new Padding(0, 0, 0, 10);
        infoBox.Controls.Add(brand);
        infoBox.Controls.Add(subtitle);

        var grid = Ui.Grid(2);
        Ui.AddRow(grid, Key("Version", "Version"), _version);
        Ui.AddRow(grid, Key("Build", "Build"), _build);
        Ui.AddRow(grid, Key("Variante", "Variant"), _variant);
        Ui.AddRow(grid, Key(".NET", ".NET"), _dotnet);
        Ui.AddRow(grid, Key("System", "System"), _os);
        Ui.AddRow(grid, Key("ADB", "ADB"), _adbPath);
        Ui.AddRow(grid, Key("ADB-Version", "ADB version"), _adbVersion);
        Ui.AddRow(grid, Key("Repo", "Repo"), _repo);
        Ui.AddRow(grid, Key("Daten", "Data"), _data);
        infoBox.Controls.Add(grid);
        foreach (Label value in new[] { _adbPath, _os, _adbVersion })
            Ui.WrapTo(value, this, 190);

        _repo.LinkClicked += (_, _) => UpdateUi.OpenUrl(Updater.RepoUrl);
        _data.LinkClicked += (_, _) =>
        {
            try { Directory.CreateDirectory(AppSettings.DataDirectory); } catch { }
            SettingsPage.OpenPath(AppSettings.DataDirectory);
        };

        var separator = new Panel { Height = 1, BackColor = Theme.CardBorder, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 12, 0, 8) };
        infoBox.Controls.Add(separator);

        var licenseTitle = Key("Lizenz", "License");
        var license = Ui.Label("Veröffentlicht unter der GNU General Public License v3 (GPL-3.0).",
                               "Released under the GNU General Public License v3 (GPL-3.0).");
        license.Margin = new Padding(0, 0, 0, 8);
        var thirdTitle = Key("Drittkomponenten", "Third-party components");
        var third = Ui.Label(
            "– Android SDK Platform-Tools (adb) von Google – nicht enthalten, eigene Lizenzbedingungen\n– .NET 8 Runtime / Windows Forms / WPF von Microsoft (MIT-Lizenz)",
            "– Android SDK Platform-Tools (adb) by Google – not included, see their own license terms\n– .NET 8 runtime / Windows Forms / WPF by Microsoft (MIT license)");
        third.Margin = new Padding(0, 0, 0, 4);
        Ui.WrapTo(third, this, 40);
        Ui.WrapTo(license, this, 40);
        infoBox.Controls.Add(licenseTitle);
        infoBox.Controls.Add(license);
        infoBox.Controls.Add(thirdTitle);
        infoBox.Controls.Add(third);

        _copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText($"ADBora {Updater.CurrentVersion.ToString(3)} (Build {Updater.BuildDate}, {Updater.KindText}, {RuntimeInformation.OSDescription.Trim()})");
                _copy.Text = Loc.T("✔ Kopiert", "✔ Copied");
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (_, _) => { t.Stop(); t.Dispose(); _copy.Text = Loc.T("Version kopieren", "Copy version"); };
                t.Start();
            }
            catch { }
        };
        _github.Click += (_, _) => UpdateUi.OpenUrl(Updater.RepoUrl);
        var infoButtons = Ui.Row(_copy, _github);
        infoButtons.Margin = new Padding(0, 14, 0, 0);
        infoBox.Controls.Add(infoButtons);
        infoCard.SetContent(infoBox);

        // --- Updates ----------------------------------------------------------
        var updateCard = new Card("Updates", "Updates") { Dock = DockStyle.Top };
        var updateBox = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Dock = DockStyle.Top };
        updateBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _updateStatus.Margin = new Padding(0, 0, 0, 8);
        Ui.WrapTo(_updateStatus, this, 40);
        var hint = Ui.Label(
            "Updates werden von GitHub geladen. Die installierte Version wird per Setup aktualisiert, die portable Version direkt im Programmordner (der Ordner „Data“ bleibt erhalten). ADBora startet danach neu.",
            "Updates are downloaded from GitHub. The installed version is updated by its setup, the portable version directly in its program folder (the \"Data\" folder is kept). ADBora restarts afterwards.",
            muted: true);
        hint.Margin = new Padding(0, 4, 0, 4);
        Ui.WrapTo(hint, this, 40);
        _checkOnStart.Checked = _state.Settings.CheckUpdatesOnStart;
        _checkOnStart.CheckedChanged += (_, _) =>
        {
            _state.Settings.CheckUpdatesOnStart = _checkOnStart.Checked;
            _state.Settings.Save();
        };
        _checkNow.Click += async (_, _) =>
        {
            if (FindForm() is not Form form) return;
            _checkNow.Enabled = false;
            try { await UpdateUi.CheckAsync(form, _state, _exitApp, manual: true); }
            finally { if (!IsDisposed) _checkNow.Enabled = true; }
        };
        _releases.Click += (_, _) => UpdateUi.OpenUrl(Updater.ReleasesUrl);
        updateBox.Controls.Add(_updateStatus);
        updateBox.Controls.Add(_checkOnStart);
        updateBox.Controls.Add(hint);
        var updateButtons = Ui.Row(_checkNow, _releases);
        updateButtons.Margin = new Padding(0, 10, 0, 0);
        updateBox.Controls.Add(updateButtons);
        updateCard.SetContent(updateBox);

        Controls.Add(updateCard);
        Controls.Add(infoCard);
    }

    private static Label Key(string de, string en)
    {
        var label = Ui.Label(de, en, bold: true);
        label.ForeColor = Theme.Accent;
        label.Margin = new Padding(0, 8, 18, 4);
        return label;
    }

    /// <summary>Do not jump to the focused control (keeps the page at the top).</summary>
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

    public void OnActivated()
    {
        Reload(true);
        // At start the page may be activated before the window exists (last tab restored).
        if (IsHandleCreated)
            BeginInvoke(new Action(() => AutoScrollPosition = Point.Empty));
        else
            AutoScrollPosition = Point.Empty;
    }

    public void CancelOperation() { }

    private void Reload(bool refreshAdb)
    {
        _version.Text = AppInfo.DisplayVersion + (Updater.PretendVersion is { } p ? $"  (Test: {p})" : "");
        _build.Text = Updater.BuildDate;
        _variant.Text = Updater.KindText;
        _dotnet.Text = RuntimeInformation.FrameworkDescription;
        _os.Text = RuntimeInformation.OSDescription.Trim();
        _adbPath.Text = _state.HasAdb ? _state.AdbPath! : Loc.T("Nicht eingerichtet (Tab „Allgemeine Einstellungen“)", "Not set up (\"General settings\" tab)");
        ShowAdbVersion();
        ShowUpdateStatus();
        if (refreshAdb && _state.HasAdb && string.IsNullOrEmpty(_state.AdbVersionText))
            _ = RefreshAdbVersionAsync();
    }

    private async Task RefreshAdbVersionAsync()
    {
        await _state.RefreshVersionAsync();
        if (!IsDisposed) ShowAdbVersion();
    }

    private void ShowAdbVersion()
    {
        string[] lines = _state.AdbVersionText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string adb = lines.FirstOrDefault(l => l.StartsWith("Android Debug Bridge", StringComparison.OrdinalIgnoreCase))?.Replace("Android Debug Bridge version", "").Trim() ?? "";
        string tools = lines.FirstOrDefault(l => l.StartsWith("Version ", StringComparison.OrdinalIgnoreCase))?[8..].Trim() ?? "";
        _adbVersion.Text = !_state.HasAdb ? "-"
            : adb.Length == 0 ? (lines.FirstOrDefault() ?? "-")
            : tools.Length > 0 ? $"{adb} · Platform-Tools {tools}" : adb;
    }

    private void ShowUpdateStatus()
    {
        string text = UpdateUi.Status;
        if (text.Length == 0)
            text = Loc.T("Noch nicht geprüft.", "Not checked yet.");
        _updateStatus.Text = text;
        _updateStatus.ForeColor = UpdateUi.Latest is { } r && Updater.IsNewer(r) ? Theme.Warning : Theme.Text;
    }
}
