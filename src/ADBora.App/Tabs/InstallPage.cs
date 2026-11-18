using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AdbTool.Backup;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>
/// Tab 3: install APKs (single or split) and copy OBB folders to the active
/// device via drag &amp; drop; open the device file system in the file manager.
/// </summary>
internal sealed class InstallPage : DockPanel, IPage
{
    private static readonly Regex PackageRe = new(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$", RegexOptions.Compiled);
    private static readonly Regex BracketPackage = new(@"\[([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?:-[0-9a-f]+)?\]", RegexOptions.Compiled);
    private static readonly Regex ObbFileName = new(@"^(main|patch)\.\d+\.(.+)\.obb$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AppState _state;
    private readonly DropZone _apkZone = new(
        "APK hierher ziehen", "Drop APK here",
        ".apk (Split-APKs werden erkannt), .apks / .xapk / .apkm, einen App-Ordner oder einen ganzen Backup-Ordner",
        ".apk (split APKs are detected), .apks / .xapk / .apkm, an app folder or a whole backup folder");
    private readonly DropZone _obbZone = new(
        "OBB-Ordner hierher ziehen", "Drop OBB folder here",
        "Ordner mit dem Paketnamen (z. B. com.firma.spiel) oder .obb-Dateien – Ziel: /sdcard/Android/obb/<Paket>",
        "Folder named after the package (e.g. com.company.game) or .obb files – target: /sdcard/Android/obb/<package>");
    private readonly CheckBox _downgrade = Ui.CheckBox("Downgrade erlauben (-d)", "Allow downgrade (-d)");
    private readonly CheckBox _grant = Ui.CheckBox("Alle Berechtigungen gewähren (-g)", "Grant all permissions (-g)");
    private readonly Button _pickApk = Ui.Button("APK auswählen …", "Choose APK …");
    private readonly Button _pickObb = Ui.Button("OBB-Ordner auswählen …", "Choose OBB folder …");
    private readonly Button _restoreButton = Ui.Button("Batch-Restore …", "Batch restore …");
    private readonly Button _explorer = Platform.IsWindows
        ? Ui.Button("Gerät im Explorer öffnen", "Open device in Explorer")
        : Ui.Button("Gerät im Dateimanager öffnen", "Open device in file manager");
    private readonly Button _cancel = Ui.Button("Abbrechen", "Cancel", ButtonKind.Danger);
    private readonly ProgressBar _progress = new() { Height = 8, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = Ui.Value("");
    private readonly TextBox _log = Ui.Console();

    private CancellationTokenSource? _cts;

    public InstallPage(AppState state)
    {
        _state = state;
        Margin = new Thickness(20, 16);

        BuildUi();

        _state.BusyChanged += UpdateEnabled;
        _state.SelectedDeviceChanged += UpdateEnabled;
        _state.AdbChanged += UpdateEnabled;
        Loc.LanguageChanged += UpdateEnabled;
        UpdateEnabled();
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private void BuildUi()
    {
        // APK card
        var apkCard = new Card("APK installieren", "Install APK");
        _apkZone.Margin = new Thickness(0, 0, 0, 8);
        _apkZone.Accepts = paths => paths.Any(p => Directory.Exists(p) || ApkBundle.IsInstallable(p));
        _apkZone.Dropped += async paths => await InstallAsync(paths);
        _apkZone.Click += async () => await PickApk();
        _pickApk.Click += async (_, _) => await PickApk();
        _downgrade.IsChecked = _state.Settings.InstallAllowDowngrade;
        _grant.IsChecked = _state.Settings.InstallGrantPermissions;
        _downgrade.IsCheckedChanged += (_, _) => { _state.Settings.InstallAllowDowngrade = _downgrade.IsChecked == true; _state.Settings.Save(); };
        _grant.IsCheckedChanged += (_, _) => { _state.Settings.InstallGrantPermissions = _grant.IsChecked == true; _state.Settings.Save(); };
        apkCard.SetContent(_apkZone);
        apkCard.SetContent(_downgrade);
        apkCard.SetContent(_grant);
        _restoreButton.Click += async (_, _) => await PickBackupFolder();
        var pickApkRow = Ui.Row(_pickApk, _restoreButton);
        pickApkRow.Margin = new Thickness(0, 8, 0, 0);
        apkCard.SetContent(pickApkRow);
        var restoreHint = Ui.Label(
            "Batch-Restore: einen ganzen Backup-Ordner laden (oder auf das Feld ziehen) und auswählen, welche Apps mit APK, OBB und Daten in einem Rutsch wiederhergestellt werden.",
            "Batch restore: load a whole backup folder (or drop it onto the field) and choose which apps to restore in one go, with APK, OBB and data.", muted: true);
        restoreHint.Margin = new Thickness(0, 6, 0, 0);
        apkCard.SetContent(restoreHint);

        // OBB card
        var obbCard = new Card("OBB kopieren", "Copy OBB");
        _obbZone.Margin = new Thickness(0, 0, 0, 8);
        _obbZone.Accepts = paths => paths.Any(p => Directory.Exists(p) || p.EndsWith(".obb", StringComparison.OrdinalIgnoreCase));
        _obbZone.Dropped += async paths => await CopyObbAsync(paths);
        _obbZone.Click += async () => await PickObb();
        _pickObb.Click += async (_, _) => await PickObb();
        obbCard.SetContent(_obbZone);
        var obbHint = Ui.Label(
            "Der Paketname wird aus dem Ordnernamen, einem Backup-Ordner „App [Paket]“ oder den .obb-Dateinamen ermittelt; sonst wird nachgefragt.",
            "The package name is taken from the folder name, a backup folder \"App [package]\" or the .obb file names; otherwise you are asked.", muted: true);
        obbHint.Margin = new Thickness(0, 4, 0, 0);
        obbCard.SetContent(obbHint);
        var pickObbRow = Ui.Row(_pickObb);
        pickObbRow.Margin = new Thickness(0, 8, 0, 0);
        obbCard.SetContent(pickObbRow);

        var root = Responsive.TwoColumns(apkCard, obbCard, breakpoint: 900, padding: default);

        // Actions: explorer, cancel, progress
        _explorer.Click += async (_, _) => await OpenInFileManager();
        _cancel.Click += (_, _) => CancelOperation();
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        Grid.SetColumn(_cancel, 1); Grid.SetColumn(_progress, 2);
        actions.Children.Add(_explorer); actions.Children.Add(_cancel); actions.Children.Add(_progress);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.Margin = new Thickness(0, 8, 0, 4);
        var explorerHint = Platform.IsWindows
            ? Ui.Label("Explorer-Zugriff nutzt MTP: Am Headset ggf. „Dateiübertragung“ bzw. den USB-Zugriff auf Dateien erlauben.",
                       "Explorer access uses MTP: on the headset allow \"File transfer\" / USB access to files if asked.", muted: true)
            : Ui.Label("Der Dateizugriff nutzt MTP (Dolphin: mtp:/, GNOME: gvfs): Am Headset ggf. „Dateiübertragung“ bzw. den USB-Zugriff auf Dateien erlauben.",
                       "File access uses MTP (Dolphin: mtp:/, GNOME: gvfs): on the headset allow \"File transfer\" / USB access to files if asked.", muted: true);
        explorerHint.Margin = new Thickness(0, 0, 0, 10);

        // Log (fills the rest)
        var logTitle = Ui.Label("Protokoll", "Log");
        logTitle.Classes.Add("section");
        logTitle.Margin = new Thickness(0, 0, 0, 8);
        Ui.Placeholder(_log, "Hier erscheinen Installations- und Kopiervorgänge.", "Install and copy operations appear here.");
        var logBody = new DockPanel();
        SetDock(logTitle, Dock.Top);
        logBody.Children.Add(logTitle);
        logBody.Children.Add(_log);
        var logCard = new Border
        {
            Background = AppTheme.Card, BorderBrush = AppTheme.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 14), MinHeight = 180, Child = logBody,
        };

        var top = new StackPanel { Children = { root, actions, _status, explorerHint } };
        SetDock(top, Dock.Top);
        Children.Add(top);
        Children.Add(logCard);
    }

    // ------------------------------------------------------------------

    public void OnActivated() => UpdateEnabled();

    public void CancelOperation()
    {
        if (_cts is null) return;
        _cts.Cancel();
        SetStatus(Loc.T("Wird abgebrochen …", "Cancelling …"));
        UpdateEnabled();
    }

    private bool DeviceReady => _state.HasAdb && _state.SelectedDevice?.IsReady == true;

    private void UpdateEnabled()
    {
        bool running = _cts is not null;
        bool idle = !_state.IsBusy;
        bool ready = DeviceReady && idle;
        _apkZone.IsEnabled = _obbZone.IsEnabled = _pickApk.IsEnabled = _pickObb.IsEnabled = _restoreButton.IsEnabled = ready;
        _explorer.IsEnabled = _state.SelectedDevice is not null;
        _cancel.IsEnabled = running && !_cts!.IsCancellationRequested;
        if (!running)
        {
            SetStatus(!_state.HasAdb
                ? Loc.T("ADB ist nicht eingerichtet (Tab „Allgemeine Einstellungen“).", "ADB is not set up (\"General settings\" tab).")
                : _state.SelectedDevice is not { IsReady: true } d
                    ? Loc.T("Kein bereites Gerät ausgewählt.", "No ready device selected.")
                    : !idle
                        ? Loc.T("Ein anderer ADB-Vorgang läuft gerade.", "Another ADB operation is running.")
                        : Loc.T($"Ziel: {d.DisplayName} ({d.ShownSerial})", $"Target: {d.DisplayName} ({d.ShownSerial})"));
        }
    }

    private void SetStatus(string text) => _status.Text = text;

    private void Log(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Log(text)); return; }
        Ui.AppendLine(_log, text);
    }

    private async Task PickApk()
    {
        if (!_apkZone.IsEnabled || Owner is not { } owner) return;
        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = Loc.T("APK auswählen", "Choose APK"),
            AllowMultiple = true,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType(Loc.T("Android-Apps", "Android apps")) { Patterns = new[] { "*.apk", "*.apks", "*.xapk", "*.apkm" } } },
        };
        var files = await owner.StorageProvider.OpenFilePickerAsync(options);
        string[] paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
            await InstallAsync(paths);
    }

    private async Task PickObb()
    {
        if (!_obbZone.IsEnabled || Owner is not { } owner) return;
        string? folder = await Dialogs.OpenFolder(owner, Loc.T("OBB-Ordner auswählen (Ordnername = Paketname)", "Choose OBB folder (folder name = package name)"));
        if (folder is not null)
            await CopyObbAsync(new[] { folder });
    }

    // ------------------------------------------------------------------
    // APK install
    // ------------------------------------------------------------------

    private sealed record InstallJob(string Label, List<string> Apks, string? ObbFolder, string? ObbPackage);

    private List<InstallJob> PlanInstall(string[] paths)
    {
        var jobs = new List<InstallJob>();
        var loose = new List<string>();

        foreach (string path in paths)
        {
            if (File.Exists(path) && ApkBundle.IsBundle(path))
            {
                jobs.Add(new InstallJob(Path.GetFileName(path), new List<string> { path }, null, null));
            }
            else if (File.Exists(path) && path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                loose.Add(path);
            }
            else if (Directory.Exists(path))
            {
                // Backup app folder "Name [package]" with APK/ (and optional OBB/)
                string apkDir = Path.Combine(path, "APK");
                string sourceDir = Directory.Exists(apkDir) ? apkDir : path;
                var apks = Directory.GetFiles(sourceDir, "*.apk").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                string obbDir = Path.Combine(path, "OBB");
                string? obbPackage = Directory.Exists(obbDir) ? PackageForFolder(obbDir) : null;
                var bundles = Directory.GetFiles(sourceDir).Where(ApkBundle.IsBundle).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (apks.Count == 0 && bundles.Count > 0)
                {
                    for (int b = 0; b < bundles.Count; b++)
                        jobs.Add(new InstallJob(Path.GetFileName(bundles[b]), new List<string> { bundles[b] },
                            b == 0 && Directory.Exists(obbDir) ? obbDir : null, b == 0 ? obbPackage : null));
                }
                else if (apks.Count > 0)
                    jobs.AddRange(GroupApks(apks, Directory.Exists(obbDir) ? obbDir : null, obbPackage));
                else
                    Log(Loc.T($"Keine APK gefunden in: {path}", $"No APK found in: {path}"));
            }
        }

        if (loose.Count > 0)
            jobs.InsertRange(0, GroupApks(loose, null, null));
        return jobs;
    }

    /// <summary>Groups APKs by package: several APKs of one package = split app.</summary>
    private List<InstallJob> GroupApks(List<string> apks, string? obbFolder, string? obbPackage)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string apk in apks)
        {
            string key;
            try { key = ApkMetadataReader.ReadPackage(apk).Package; }
            catch { key = ""; }
            if (key.Length == 0) key = "?" + apk; // unreadable: install on its own
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add(apk);
        }

        var jobs = new List<InstallJob>();
        bool obbAssigned = false;
        foreach (var (package, files) in groups)
        {
            string label = package.StartsWith('?') ? Path.GetFileName(files[0]) : package;
            bool withObb = obbFolder is not null && !obbAssigned && (obbPackage is null || obbPackage == package || groups.Count == 1);
            if (withObb) obbAssigned = true;
            jobs.Add(new InstallJob(label, files, withObb ? obbFolder : null,
                withObb ? (obbPackage ?? (package.StartsWith('?') ? null : package)) : null));
        }
        return jobs;
    }

    private async Task InstallAsync(string[] paths)
    {
        AdbDevice? device = _state.SelectedDevice;
        if (!DeviceReady || device is null)
            return;

        if (paths.Length == 1 && Directory.Exists(paths[0]) && BackupScanner.ContainsAppFolders(paths[0]))
        {
            await OpenRestore(paths[0]);
            return;
        }

        List<InstallJob> jobs;
        try { jobs = PlanInstall(paths); }
        catch (Exception ex) { Log(ex.Message); return; }
        if (jobs.Count == 0) return;

        if (!_state.TryBeginOperation(Loc.T("APK-Installation läuft", "APK installation running")))
            return;

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        UpdateEnabled();
        var adb = new AdbClient(_state.AdbPath!, device.Serial);
        int ok = 0, failed = 0;

        try
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                InstallJob job = jobs[i];
                bool split = job.Apks.Count > 1;
                _progress.IsIndeterminate = true;
                SetStatus(Loc.T($"Installiere {job.Label} ({i + 1}/{jobs.Count}) …", $"Installing {job.Label} ({i + 1}/{jobs.Count}) …"));
                Log("");
                Log(Loc.T($"▶ Installiere {job.Label} auf {device.DisplayName}", $"▶ Installing {job.Label} on {device.DisplayName}") +
                    (split ? Loc.T($" · Split-App, {job.Apks.Count} APKs", $" · split app, {job.Apks.Count} APKs") : ""));
                foreach (string apk in job.Apks)
                    Log($"   {Path.GetFileName(apk)}  ({new FileInfo(apk).Length / 1024.0 / 1024.0:0.0} MB)");

                if (await InstallApksAsync(adb, job.Apks, token))
                {
                    ok++;
                    if (job.ObbFolder is not null)
                    {
                        string? package = job.ObbPackage ?? await AskPackage(job.ObbFolder);
                        if (package is not null)
                            await PushObbFolderAsync(adb, job.ObbFolder, package, token);
                    }
                }
                else
                {
                    failed++;
                }
            }

            SetStatus(Loc.T($"Fertig: {ok} installiert, {failed} fehlgeschlagen.", $"Done: {ok} installed, {failed} failed."));
            if (failed > 0)
                await Dialogs.Message(Owner, Loc.T($"{failed} Installation(en) fehlgeschlagen. Details stehen im Protokoll.",
                    $"{failed} installation(s) failed. See the log for details."), "ADBora", MessageKind.Warning);
        }
        catch (OperationCanceledException)
        {
            Log(Loc.T("Abgebrochen.", "Cancelled."));
            SetStatus(Loc.T("Abgebrochen.", "Cancelled."));
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            await Dialogs.Message(Owner, ex.Message, "ADBora", MessageKind.Error);
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>
    /// adb install / install-multiple with the chosen options; logs the result.
    /// .apks / .xapk / .apkm bundles are unpacked first (OBB of an .xapk is copied after the install).
    /// </summary>
    private async Task<bool> InstallApksAsync(AdbClient adb, List<string> files, CancellationToken token)
    {
        var apks = new List<string>();
        var temps = new List<string>();
        var obbs = new List<(string Folder, string Package)>();
        try
        {
            foreach (string file in files)
            {
                if (!ApkBundle.IsBundle(file)) { apks.Add(file); continue; }
                Log(Loc.T($"   Entpacke {Path.GetFileName(file)} …", $"   Unpacking {Path.GetFileName(file)} …"));
                ApkBundle.Expanded expanded = await Task.Run(() => ApkBundle.Expand(file, AppSettings.TempDirectory), token);
                temps.Add(Path.GetDirectoryName(expanded.Apks[0])!);
                apks.AddRange(expanded.Apks);
                Log("   " + string.Join(", ", expanded.Apks.Select(Path.GetFileName)));
                if (expanded.ObbFolder is not null && expanded.ObbPackage is not null)
                    obbs.Add((expanded.ObbFolder, expanded.ObbPackage));
            }

            bool installed = await RunInstallAsync(adb, apks, streaming: true, token);
            if (installed)
            {
                foreach (var (folder, package) in obbs)
                    await PushTreeAsync(adb, folder, $"/sdcard/Android/obb/{package}", "OBB", token, viaStaging: true);
            }
            return installed;
        }
        catch (AdbException ex)
        {
            Log("✖ " + ex.Message);
            return false;
        }
        finally
        {
            foreach (string t in temps) ApkBundle.TryDelete(t);
        }
    }

    private async Task<bool> RunInstallAsync(AdbClient adb, List<string> apks, bool streaming, CancellationToken token)
    {
        var args = new List<string> { apks.Count > 1 ? "install-multiple" : "install", "-r" };
        if (_downgrade.IsChecked == true) args.Add("-d");
        if (_grant.IsChecked == true) args.Add("-g");
        if (!streaming) args.Add("--no-streaming");
        args.AddRange(apks);

        bool success = false;
        bool packageManagerError = false;
        var sw = Stopwatch.StartNew();
        int exit = await adb.RunStreamingAsync(args, (line, _) =>
        {
            string t = line.Trim();
            if (t.StartsWith("Success", StringComparison.OrdinalIgnoreCase)) success = true;
            if (t.Contains("INSTALL_FAILED_", StringComparison.Ordinal) || t.Contains("INSTALL_PARSE_FAILED_", StringComparison.Ordinal))
                packageManagerError = true;
            Log("   " + line);
        }, token);

        if (exit == 0 && success)
        {
            Log(Loc.T($"✔ Installiert ({sw.Elapsed.TotalSeconds:0.0} s)", $"✔ Installed ({sw.Elapsed.TotalSeconds:0.0} s)"));
            return true;
        }

        // Transfer problems (e.g. "Broken pipe" during a streamed install) often succeed on a second,
        // non-streamed attempt. Real package manager errors (INSTALL_FAILED_…) are not retried.
        if (streaming && !packageManagerError)
        {
            Log(Loc.T("   Übertragung fehlgeschlagen – zweiter Versuch ohne Streaming …", "   Transfer failed – second attempt without streaming …"));
            await Task.Delay(1500, token);
            return await RunInstallAsync(adb, apks, streaming: false, token);
        }

        Log(Loc.T("✖ Installation fehlgeschlagen", "✖ Installation failed"));
        return false;
    }

    // ------------------------------------------------------------------
    // Batch restore
    // ------------------------------------------------------------------

    private async Task PickBackupFolder()
    {
        if (!_restoreButton.IsEnabled || Owner is not { } owner) return;
        string backups = Privacy.Expand(_state.Settings.BackupDestination);
        string? folder = await Dialogs.OpenFolder(owner, Loc.T("Backup-Ordner auswählen (Backup_… oder der Ordner mit mehreren Backups)", "Choose backup folder (Backup_… or the folder containing several backups)"),
            Directory.Exists(backups) ? backups : null);
        if (folder is not null)
            await OpenRestore(folder);
    }

    private async Task OpenRestore(string folder)
    {
        AdbDevice? device = _state.SelectedDevice;
        if (!DeviceReady || device is null || _state.IsBusy || Owner is not { } owner)
            return;

        List<RestoreEntry> entries;
        try { entries = await Task.Run(() => BackupScanner.Scan(folder)); }
        catch (Exception ex)
        {
            await Dialogs.Message(owner, ex.Message, "ADBora", MessageKind.Error);
            return;
        }

        if (entries.Count == 0)
        {
            await Dialogs.Message(owner, Loc.T($"In diesem Ordner wurde kein App-Backup gefunden:\n{folder}\n\nErwartet werden App-Ordner „Name [Paket]“ mit APK/, OBB/ oder Data/.",
                    $"No app backup was found in this folder:\n{folder}\n\nExpected are app folders \"Name [package]\" containing APK/, OBB/ or Data/."));
            return;
        }

        List<RestoreEntry>? selected = await RestoreDialog.Show(owner, folder, entries, $"{device.DisplayName} ({device.ShownSerial})");
        if (selected is { Count: > 0 })
            await RestoreAsync(selected);
    }

    private async Task RestoreAsync(List<RestoreEntry> items)
    {
        AdbDevice? device = _state.SelectedDevice;
        if (!DeviceReady || device is null)
            return;
        if (!_state.TryBeginOperation(Loc.T("Batch-Restore läuft", "Batch restore running")))
            return;

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        UpdateEnabled();
        var adb = new AdbClient(_state.AdbPath!, device.Serial);
        var failed = new List<string>();
        int steps = items.Sum(i => (i.Apk ? 1 : 0) + (i.Obb ? 1 : 0) + (i.Data ? 1 : 0));
        int step = 0;
        _progress.IsIndeterminate = false;
        _progress.Maximum = Math.Max(1, steps);
        _progress.Value = 0;
        var total = Stopwatch.StartNew();

        Log("");
        Log(Loc.T($"══ Batch-Restore: {items.Count} App(s) → {device.DisplayName} ({device.ShownSerial})",
                  $"══ Batch restore: {items.Count} app(s) → {device.DisplayName} ({device.ShownSerial})"));

        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                RestoreEntry item = items[i];
                string label = $"{item.Name} ({i + 1}/{items.Count})";
                Log("");
                Log($"▶ {item.Name} [{item.Package}] · " + string.Join(" + ", new[] { item.Apk ? "APK" : null, item.Obb ? "OBB" : null, item.Data ? Loc.T("Daten", "Data") : null }.Where(k => k is not null)));

                try
                {
                    if (item.Apk)
                    {
                        SetStatus(Loc.T($"Installiere {label} …", $"Installing {label} …"));
                        foreach (string apk in item.Apks)
                            Log($"   {Path.GetFileName(apk)}  ({new FileInfo(apk).Length / 1024.0 / 1024.0:0.0} MB)");
                        bool installed = await InstallApksAsync(adb, item.Apks, token);
                        _progress.Value = ++step;
                        if (!installed)
                        {
                            failed.Add(item.Name);
                            int skipped = (item.Obb ? 1 : 0) + (item.Data ? 1 : 0);
                            if (skipped > 0) Log(Loc.T("   OBB/Daten übersprungen, da die Installation fehlgeschlagen ist.", "   OBB/data skipped because the installation failed."));
                            step += skipped;
                            _progress.Value = step;
                            continue;
                        }
                    }
                    if (item.Obb && item.ObbDir is not null)
                    {
                        SetStatus(Loc.T($"Kopiere OBB: {label} …", $"Copying OBB: {label} …"));
                        await PushTreeAsync(adb, item.ObbDir, $"/sdcard/Android/obb/{item.Package}", "OBB", token, viaStaging: true);
                        _progress.Value = ++step;
                    }
                    if (item.Data && item.DataDir is not null)
                    {
                        SetStatus(Loc.T($"Kopiere Daten: {label} …", $"Copying data: {label} …"));
                        await adb.CaptureAsync(token, "shell", AdbClient.ShellJoin(new[] { "am", "force-stop", item.Package }));
                        await PushTreeAsync(adb, item.DataDir, $"/sdcard/Android/data/{item.Package}", Loc.T("Daten", "Data"), token, viaStaging: true);
                    }
                    if (item.Data && item.InternalTar is not null)
                        await RestoreInternalAsync(adb, item.InternalTar, item.Package, token);
                    if (item.Data && (item.DataDir is not null || item.InternalTar is not null))
                        _progress.Value = ++step;
                }
                catch (AdbException ex)
                {
                    failed.Add(item.Name);
                    Log("✖ " + ex.Message);
                }
            }

            int ok = items.Count - failed.Count;
            string done = Loc.T($"Batch-Restore fertig: {ok} von {items.Count} Apps erfolgreich ({total.Elapsed:mm\\:ss}).",
                                $"Batch restore finished: {ok} of {items.Count} apps successful ({total.Elapsed:mm\\:ss}).");
            Log("");
            Log("══ " + done);
            SetStatus(done);
            if (failed.Count > 0)
                await Dialogs.Message(Owner, Loc.T($"Bei {failed.Count} App(s) ist ein Fehler aufgetreten:\n\n", $"{failed.Count} app(s) failed:\n\n") +
                    string.Join("\n", failed.Take(15)) + (failed.Count > 15 ? "\n…" : "") +
                    Loc.T("\n\nDetails stehen im Protokoll.", "\n\nSee the log for details."), "ADBora", MessageKind.Warning);
        }
        catch (OperationCanceledException)
        {
            Log(Loc.T("Abgebrochen – die aktuelle App ist ggf. nur teilweise wiederhergestellt.", "Cancelled – the current app may be restored only partially."));
            SetStatus(Loc.T("Abgebrochen.", "Cancelled."));
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            await Dialogs.Message(Owner, ex.Message, "ADBora", MessageKind.Error);
        }
        finally
        {
            Finish();
        }
    }

    private const string StagingDir = "/data/local/tmp/adbora-restore";

    /// <summary>Writes Internal/data.tar back into /data/data/&lt;package&gt; via run-as (debuggable apps only).</summary>
    private async Task RestoreInternalAsync(AdbClient adb, string tar, string package, CancellationToken token)
    {
        const string remote = "/data/local/tmp/adbora-internal.tar";
        SetStatus(Loc.T("Kopiere interne App-Daten …", "Copying internal app data …"));
        Log(Loc.T($"   Intern → /data/data/{package} (run-as)", $"   Internal → /data/data/{package} (run-as)"));
        try
        {
            int exit = await adb.RunStreamingAsync(new[] { "push", tar, remote }, (line, _) => { if (line.Trim().Length > 0) Log("   " + line.Trim()); }, token);
            if (exit != 0)
                throw new AdbException(Loc.T("Interne Daten: Kopieren fehlgeschlagen", "Internal data: copy failed"));
            AdbResult r = await adb.CaptureAsync(token, TimeSpan.FromMinutes(30), "shell", AdbTool.Backup.BackupService.InternalRestoreCommand(package, remote));
            if (!r.Output.Contains("ADBORA_OK"))
                throw new AdbException(Loc.T($"Interne Daten konnten nicht zurückgeschrieben werden (App nicht debuggable?)\n{r.Combined}",
                                             $"Internal data could not be restored (app not debuggable?)\n{r.Combined}"));
            Log(Loc.T("✔ Interne App-Daten wiederhergestellt", "✔ Internal app data restored"));
        }
        finally
        {
            try { await adb.CaptureAsync(CancellationToken.None, "shell", "rm -f " + remote); } catch { }
        }
    }

    /// <summary>
    /// Copies the content of a local folder into a device folder. Each top-level
    /// entry is pushed with one adb call (fast for data folders with many files).
    /// viaStaging: newer Android versions refuse "adb push" into Android/data,
    /// so the files are pushed to /data/local/tmp first and copied by the shell.
    /// </summary>
    private async Task PushTreeAsync(AdbClient adb, string folder, string target, string kind, CancellationToken token, bool viaStaging = false)
    {
        FileSystemInfo[] entries = new DirectoryInfo(folder).GetFileSystemInfos();
        var sizes = entries.ToDictionary(e => e.FullName, e => e is FileInfo f ? f.Length
            : Directory.EnumerateFiles(e.FullName, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length));
        long total = Math.Max(1, sizes.Values.Sum());
        Log(Loc.T($"   {kind} → {target}  ({total / 1024.0 / 1024.0:0.0} MB)", $"   {kind} → {target}  ({total / 1024.0 / 1024.0:0.0} MB)"));

        AdbResult mk = await adb.CaptureAsync(token, "shell", AdbClient.ShellJoin(new[] { "mkdir", "-p", target }));
        if (!mk.Ok)
            throw new AdbException(Loc.T($"Ordner konnte nicht angelegt werden: {target}\n{mk.Combined}", $"Could not create folder: {target}\n{mk.Combined}"));

        var sw = Stopwatch.StartNew();
        string pushTarget = target;
        if (viaStaging)
        {
            pushTarget = StagingDir;
            await adb.CaptureAsync(token, "shell", AdbClient.ShellJoin(new[] { "rm", "-rf", StagingDir }));
            AdbResult st = await adb.CaptureAsync(token, "shell", AdbClient.ShellJoin(new[] { "mkdir", "-p", StagingDir }));
            if (!st.Ok)
                throw new AdbException(Loc.T($"Zwischenordner konnte nicht angelegt werden: {StagingDir}\n{st.Combined}", $"Could not create staging folder: {StagingDir}\n{st.Combined}"));
        }

        try
        {
            foreach (FileSystemInfo entry in entries)
            {
                token.ThrowIfCancellationRequested();
                int exit = await adb.RunStreamingAsync(new[] { "push", entry.FullName, pushTarget + "/" }, (line, _) =>
                {
                    if (line.Trim().Length > 0) Log("   " + line.Trim());
                }, token);
                if (exit != 0)
                    throw new AdbException(Loc.T($"{kind}: Kopieren fehlgeschlagen: {entry.Name}", $"{kind}: copy failed: {entry.Name}"));
            }

            if (viaStaging)
            {
                AdbResult cp = await adb.CaptureAsync(token, TimeSpan.FromHours(1), "shell",
                    $"cp -r {AdbClient.ShellQuote(StagingDir + "/.")} {AdbClient.ShellQuote(target + "/")} && echo ADBORA_OK");
                if (!cp.Output.Contains("ADBORA_OK"))
                    throw new AdbException(Loc.T($"{kind}: Kopieren auf dem Gerät fehlgeschlagen:\n{cp.Combined}", $"{kind}: copy on the device failed:\n{cp.Combined}"));
                // Restored files belong to the shell user – make them readable / writable for the app
                await adb.CaptureAsync(token, TimeSpan.FromMinutes(10), "shell", AdbTool.Backup.BackupService.FixPermissionsCommand(target));
            }
        }
        finally
        {
            if (viaStaging)
            {
                try { await adb.CaptureAsync(CancellationToken.None, "shell", AdbClient.ShellJoin(new[] { "rm", "-rf", StagingDir })); } catch { }
            }
        }
        double seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        Log(Loc.T($"✔ {kind} kopiert ({seconds:0.0} s, {total / 1024.0 / 1024.0 / seconds:0.0} MB/s)",
                  $"✔ {kind} copied ({seconds:0.0} s, {total / 1024.0 / 1024.0 / seconds:0.0} MB/s)"));
    }

    // ------------------------------------------------------------------
    // OBB copy
    // ------------------------------------------------------------------

    /// <summary>Determines the package for an OBB folder, or null if unknown.</summary>
    private static string? PackageForFolder(string folder)
    {
        var dir = new DirectoryInfo(folder);
        if (PackageRe.IsMatch(dir.Name))
            return dir.Name;

        // Backup layout: "App [package]/OBB" or "App [package]" itself
        foreach (DirectoryInfo? candidate in new[] { dir, dir.Parent })
        {
            if (candidate is null) continue;
            Match m = BracketPackage.Match(candidate.Name);
            if (m.Success) return m.Groups[1].Value;
            string manifest = Path.Combine(candidate.FullName, "manifest.json");
            try
            {
                if (File.Exists(manifest) && JsonNode.Parse(File.ReadAllText(manifest))?["package"]?.GetValue<string>() is { } pkg && PackageRe.IsMatch(pkg))
                    return pkg;
            }
            catch { }
        }

        // main.<version>.<package>.obb inside the folder
        foreach (string file in Directory.EnumerateFiles(folder, "*.obb", SearchOption.AllDirectories))
        {
            Match m = ObbFileName.Match(Path.GetFileName(file));
            if (m.Success && PackageRe.IsMatch(m.Groups[2].Value))
                return m.Groups[2].Value;
        }
        return null;
    }

    private async Task<string?> AskPackage(string folder)
    {
        if (Owner is not { } owner) return null;
        while (true)
        {
            var r = await InputDialog.Show(owner, Loc.T("Paketname angeben", "Enter package name"),
                Loc.T($"Für „{Path.GetFileName(folder)}“ konnte kein Paketname ermittelt werden.\nPaketname der App (z. B. com.firma.spiel):",
                      $"No package name could be determined for \"{Path.GetFileName(folder)}\".\nPackage name of the app (e.g. com.company.game):"), "");
            if (r is null) return null;
            string value = r.Value.Text.Trim();
            if (PackageRe.IsMatch(value)) return value;
            await Dialogs.Message(owner, Loc.T("Ungültiger Paketname.", "Invalid package name."));
        }
    }

    private async Task CopyObbAsync(string[] paths)
    {
        AdbDevice? device = _state.SelectedDevice;
        if (!DeviceReady || device is null)
            return;

        // Folders: package from name/backup; loose .obb files: package from file name
        var plan = new List<(string Package, string Folder, List<string> Files)>();
        foreach (string path in paths.Where(Directory.Exists))
        {
            string? package = PackageForFolder(path) ?? await AskPackage(path);
            if (package is null) continue;
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList();
            if (files.Count == 0)
            {
                Log(Loc.T($"Ordner ist leer: {path}", $"Folder is empty: {path}"));
                continue;
            }
            plan.Add((package, path, files));
        }
        var looseObb = paths.Where(p => File.Exists(p) && p.EndsWith(".obb", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var group in looseObb.GroupBy(f => ObbFileName.Match(Path.GetFileName(f)) is { Success: true } m ? m.Groups[2].Value : ""))
        {
            string? package = PackageRe.IsMatch(group.Key) ? group.Key : await AskPackage(group.First());
            if (package is null) continue;
            plan.Add((package, Path.GetDirectoryName(group.First())!, group.ToList()));
        }
        if (plan.Count == 0) return;

        if (!_state.TryBeginOperation(Loc.T("OBB wird kopiert", "Copying OBB")))
            return;

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        UpdateEnabled();
        var adb = new AdbClient(_state.AdbPath!, device.Serial);

        try
        {
            foreach (var (package, folder, files) in plan)
                await PushObbFilesAsync(adb, folder, files, package, token);
            SetStatus(Loc.T("OBB-Kopie abgeschlossen.", "OBB copy finished."));
        }
        catch (OperationCanceledException)
        {
            Log(Loc.T("Abgebrochen – auf dem Gerät können unvollständige Dateien liegen.", "Cancelled – incomplete files may remain on the device."));
            SetStatus(Loc.T("Abgebrochen.", "Cancelled."));
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            await Dialogs.Message(Owner, ex.Message, "ADBora", MessageKind.Error);
        }
        finally
        {
            Finish();
        }
    }

    private Task PushObbFolderAsync(AdbClient adb, string folder, string package, CancellationToken token) =>
        PushObbFilesAsync(adb, folder, Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList(), package, token);

    /// <summary>Copies files to /sdcard/Android/obb/&lt;package&gt;/ keeping sub folders.</summary>
    private async Task PushObbFilesAsync(AdbClient adb, string folder, List<string> files, string package, CancellationToken token)
    {
        string target = $"/sdcard/Android/obb/{package}";
        long total = Math.Max(1, files.Sum(f => new FileInfo(f).Length));
        long done = 0;

        Log("");
        Log(Loc.T($"▶ OBB → {target}  ({files.Count} Datei(en), {total / 1024.0 / 1024.0:0.0} MB)",
                  $"▶ OBB → {target}  ({files.Count} file(s), {total / 1024.0 / 1024.0:0.0} MB)"));

        AdbResult installed = await adb.CaptureAsync(token, "shell", AdbClient.ShellJoin(new[] { "pm", "list", "packages", package }));
        if (!installed.Output.Split('\n').Any(l => l.Trim() == "package:" + package))
            Log(Loc.T($"   Hinweis: {package} ist auf dem Gerät (noch) nicht installiert.", $"   Note: {package} is not installed on the device (yet)."));

        var createdDirs = new HashSet<string>(StringComparer.Ordinal);
        _progress.IsIndeterminate = false;
        _progress.Maximum = 1000;
        _progress.Value = 0;
        var sw = Stopwatch.StartNew();

        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            string remote = $"{target}/{relative}";
            string remoteDir = remote[..remote.LastIndexOf('/')];
            if (createdDirs.Add(remoteDir))
            {
                AdbResult mk = await adb.CaptureAsync(token, "shell", AdbClient.ShellJoin(new[] { "mkdir", "-p", remoteDir }));
                if (!mk.Ok) throw new AdbException(Loc.T($"Ordner konnte nicht angelegt werden: {remoteDir}\n{mk.Combined}", $"Could not create folder: {remoteDir}\n{mk.Combined}"));
            }

            SetStatus(Loc.T($"Kopiere {relative} …", $"Copying {relative} …"));
            long size = new FileInfo(file).Length;
            int exit = await adb.RunStreamingAsync(new[] { "push", file, remote }, (line, _) => Log("   " + line), token);
            if (exit != 0)
                throw new AdbException(Loc.T($"Kopieren fehlgeschlagen: {relative}", $"Copy failed: {relative}"));
            done += size;
            _progress.Value = (int)(done * 1000 / total);
        }

        double seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        Log(Loc.T($"✔ OBB kopiert ({seconds:0.0} s, {total / 1024.0 / 1024.0 / seconds:0.0} MB/s)",
                  $"✔ OBB copied ({seconds:0.0} s, {total / 1024.0 / 1024.0 / seconds:0.0} MB/s)"));
    }

    private void Finish()
    {
        string result = _status.Text ?? ""; // keep the result message visible
        _cts?.Dispose();
        _cts = null;
        _progress.IsIndeterminate = false;
        _state.EndOperation();
        UpdateEnabled();
        _status.Text = result;
    }

    // ------------------------------------------------------------------
    // File manager (MTP)
    // ------------------------------------------------------------------

    private async Task OpenInFileManager()
    {
        AdbDevice? device = _state.SelectedDevice;
        if (device is null) return;
        string? path = await Task.Run(() => MtpLocator.Find(device));
        try
        {
            if (path is not null)
            {
                MtpLocator.Open(path);
                Log(Loc.T($"Dateimanager geöffnet: {device.DisplayName}", $"File manager opened: {device.DisplayName}"));
            }
            else
            {
                MtpLocator.OpenOverview();
                Log(Platform.IsWindows
                    ? Loc.T("Gerät nicht unter „Dieser PC“ gefunden – „Dieser PC“ wurde geöffnet. Am Headset ggf. den Dateizugriff (MTP) erlauben.",
                            "Device not found under \"This PC\" – opened \"This PC\". Allow file access (MTP) on the headset if asked.")
                    : Loc.T("MTP-Geräteübersicht geöffnet (mtp:/). Erscheint das Headset nicht: am Headset den Dateizugriff (MTP) erlauben.",
                            "Opened the MTP device overview (mtp:/). If the headset does not appear: allow file access (MTP) on the headset."));
            }
        }
        catch (Exception ex)
        {
            Log(ex.Message);
        }
    }
}
