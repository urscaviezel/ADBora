using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>Tab 8: build information, licence notes and update check.</summary>
internal sealed class InfoPage : ScrollViewer, IPage
{
    private readonly AppState _state;
    private readonly Action _exitApp;
    private readonly TextBlock _version = Ui.Value();
    private readonly TextBlock _build = Ui.Value();
    private readonly TextBlock _variant = Ui.Value();
    private readonly TextBlock _dotnet = Ui.Value();
    private readonly TextBlock _os = Ui.Value();
    private readonly TextBlock _adbPath = Ui.Value();
    private readonly TextBlock _adbVersion = Ui.Value();
    private readonly TextBlock _repo = Link(Updater.RepoUrl);
    private readonly TextBlock _data = Link(AppSettings.DataDirectory);
    private readonly TextBlock _updateStatus = Ui.Value("");
    private readonly CheckBox _checkOnStart = Ui.CheckBox("Beim Start automatisch nach Updates suchen", "Check for updates automatically at start");
    private readonly Button _checkNow = Ui.Button("Nach Updates suchen", "Check for updates", ButtonKind.Primary);
    private readonly Button _copy = Ui.Button("Version kopieren", "Copy version");
    private readonly Button _github = Ui.Button("GitHub öffnen", "Open GitHub");
    private readonly Button _releases = Ui.Button("Alle Versionen", "All releases");

    public InfoPage(AppState state, Action exitApp)
    {
        _state = state;
        _exitApp = exitApp;
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;

        BuildUi();
        Reload(false);

        UpdateUi.StatusChanged += () => Dispatcher.UIThread.Post(ShowUpdateStatus);
        Loc.LanguageChanged += () => Reload(false);
        _state.AdbChanged += () => Reload(false);
    }

    private static TextBlock Link(string text)
    {
        var link = Ui.Value(Privacy.MaskPaths(text));
        link.Foreground = AppTheme.Accent;
        link.Cursor = new Cursor(StandardCursorType.Hand);
        link.PointerEntered += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.PointerExited += (_, _) => link.TextDecorations = null;
        return link;
    }

    private void BuildUi()
    {
        // --- Build info -----------------------------------------------------
        var infoCard = new Card();
        var subtitle = Ui.Label("Android Device Toolbox", "Android Device Toolbox", muted: true);
        subtitle.Margin = new Thickness(0, 0, 0, 10);
        infoCard.SetContent(new BrandLabel());
        infoCard.SetContent(subtitle);

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
        infoCard.SetContent(grid);

        _repo.PointerPressed += (_, _) => Platform.Open(Updater.RepoUrl);
        _data.PointerPressed += (_, _) =>
        {
            try { Directory.CreateDirectory(AppSettings.DataDirectory); } catch { }
            Platform.OpenFolder(AppSettings.DataDirectory);
        };
        Privacy.Changed += () => _data.Text = Privacy.MaskPaths(AppSettings.DataDirectory);

        infoCard.SetContent(new Border { Height = 1, Background = AppTheme.CardBorder, Margin = new Thickness(0, 12, 0, 8) });

        var license = Ui.Label("Veröffentlicht unter der GNU General Public License v3 (GPL-3.0).",
                               "Released under the GNU General Public License v3 (GPL-3.0).");
        license.Margin = new Thickness(0, 0, 0, 8);
        var third = Ui.Label(
            "– Android SDK Platform-Tools (adb) von Google – nicht enthalten, eigene Lizenzbedingungen\n– scrcpy von Genymobile (Apache-2.0) – für den Stream, wird bei Bedarf separat geladen\n– FFmpeg (LGPL) – Video-/Tondecoder, aus dem scrcpy-Paket (Windows) bzw. vom System (Linux)\n– .NET 10 Runtime von Microsoft, Avalonia UI, SkiaSharp (alle MIT-Lizenz)",
            "– Android SDK Platform-Tools (adb) by Google – not included, see their own license terms\n– scrcpy by Genymobile (Apache-2.0) – for streaming, downloaded separately when needed\n– FFmpeg (LGPL) – video/audio decoder, from the scrcpy package (Windows) or the system (Linux)\n– .NET 10 runtime by Microsoft, Avalonia UI, SkiaSharp (all MIT license)");
        third.Margin = new Thickness(0, 0, 0, 4);
        infoCard.SetContent(Key("Lizenz", "License"));
        infoCard.SetContent(license);
        infoCard.SetContent(Key("Drittkomponenten", "Third-party components"));
        infoCard.SetContent(third);

        _copy.Click += async (_, _) =>
        {
            await Dialogs.CopyText(this, $"ADBora {Updater.CurrentVersion.ToString(3)} (Build {Updater.BuildDate}, {Updater.KindText}, {RuntimeInformation.OSDescription.Trim()})");
            _copy.Content = Loc.T("✔ Kopiert", "✔ Copied");
            DispatcherTimer.RunOnce(() => _copy.Content = Loc.T("Version kopieren", "Copy version"), TimeSpan.FromMilliseconds(1500));
        };
        _github.Click += (_, _) => Platform.Open(Updater.RepoUrl);
        var infoButtons = Ui.Row(_copy, _github);
        infoButtons.Margin = new Thickness(0, 14, 0, 0);
        infoCard.SetContent(infoButtons);

        // --- Updates ----------------------------------------------------------
        var updateCard = new Card("Updates", "Updates");
        _updateStatus.Margin = new Thickness(0, 0, 0, 8);
        var hint = Platform.IsWindows
            ? Ui.Label(
                "Updates werden von GitHub geladen. Die installierte Version wird per Setup aktualisiert, die portable Version direkt im Programmordner (der Ordner „Data“ bleibt erhalten). ADBora startet danach neu.",
                "Updates are downloaded from GitHub. The installed version is updated by its setup, the portable version directly in its program folder (the \"Data\" folder is kept). ADBora restarts afterwards.",
                muted: true)
            : Ui.Label(
                "Updates werden von GitHub geladen und direkt im Programmordner eingespielt (Einstellungen und der Ordner „Data“ bleiben erhalten). ADBora startet danach neu. Über die Paketverwaltung installierte Kopien werden von dieser aktualisiert.",
                "Updates are downloaded from GitHub and applied directly in the program folder (settings and the \"Data\" folder are kept). ADBora restarts afterwards. Copies installed by a package manager are updated by it.",
                muted: true);
        hint.Margin = new Thickness(0, 4, 0, 4);
        _checkOnStart.IsChecked = _state.Settings.CheckUpdatesOnStart;
        _checkOnStart.IsCheckedChanged += (_, _) =>
        {
            _state.Settings.CheckUpdatesOnStart = _checkOnStart.IsChecked == true;
            _state.Settings.Save();
        };
        _checkNow.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window window) return;
            _checkNow.IsEnabled = false;
            try { await UpdateUi.CheckAsync(window, _state, _exitApp, manual: true); }
            finally { _checkNow.IsEnabled = true; }
        };
        _releases.Click += (_, _) => Platform.Open(Updater.ReleasesUrl);
        updateCard.SetContent(_updateStatus);
        updateCard.SetContent(_checkOnStart);
        updateCard.SetContent(hint);
        var updateButtons = Ui.Row(_checkNow, _releases);
        updateButtons.Margin = new Thickness(0, 10, 0, 0);
        updateCard.SetContent(updateButtons);

        Content = new StackPanel { Margin = new Thickness(20, 16), Children = { infoCard, updateCard } };
    }

    private static TextBlock Key(string de, string en)
    {
        var label = Ui.Label(de, en, bold: true);
        label.Foreground = AppTheme.Accent;
        label.Margin = new Thickness(0, 6, 18, 4);
        return label;
    }

    public void OnActivated()
    {
        Reload(true);
        Offset = default;
    }

    public void CancelOperation() { }

    private void Reload(bool refreshAdb)
    {
        _version.Text = AppInfo.DisplayVersion + (Updater.PretendVersion is { } p ? $"  (Test: {p})" : "");
        _build.Text = Updater.BuildDate;
        _variant.Text = Updater.KindText;
        _dotnet.Text = RuntimeInformation.FrameworkDescription;
        _os.Text = RuntimeInformation.OSDescription.Trim() + $" ({RuntimeInformation.OSArchitecture})";
        _adbPath.Text = _state.HasAdb ? Privacy.MaskPaths(_state.AdbPath!) : Loc.T("Nicht eingerichtet (Tab „Allgemeine Einstellungen“)", "Not set up (\"General settings\" tab)");
        ShowAdbVersion();
        ShowUpdateStatus();
        if (refreshAdb && _state.HasAdb && string.IsNullOrEmpty(_state.AdbVersionText))
            _ = RefreshAdbVersionAsync();
    }

    private async Task RefreshAdbVersionAsync()
    {
        await _state.RefreshVersionAsync();
        ShowAdbVersion();
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
        _updateStatus.Foreground = UpdateUi.Latest is { } r && Updater.IsNewer(r) ? AppTheme.Warning : AppTheme.Text;
    }
}
