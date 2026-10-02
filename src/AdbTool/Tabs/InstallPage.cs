using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AdbTool.Backup;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>
/// Tab 3: install APKs (single or split) and copy OBB folders to the active
/// device via drag &amp; drop; open the device file system in Explorer.
/// </summary>
internal sealed class InstallPage : UserControl, IPage
{
    private static readonly Regex PackageRe = new(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$", RegexOptions.Compiled);
    private static readonly Regex BracketPackage = new(@"\[([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?:-[0-9a-f]+)?\]", RegexOptions.Compiled);
    private static readonly Regex ObbFileName = new(@"^(main|patch)\.\d+\.(.+)\.obb$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AppState _state;
    private readonly DropZone _apkZone = new("",
        "APK hierher ziehen", "Drop APK here",
        ".apk (Split-APKs werden erkannt), .apks / .xapk / .apkm, einen App-Ordner oder einen ganzen Backup-Ordner",
        ".apk (split APKs are detected), .apks / .xapk / .apkm, an app folder or a whole backup folder");
    private readonly DropZone _obbZone = new("",
        "OBB-Ordner hierher ziehen", "Drop OBB folder here",
        "Ordner mit dem Paketnamen (z. B. com.firma.spiel) oder .obb-Dateien – Ziel: /sdcard/Android/obb/<Paket>",
        "Folder named after the package (e.g. com.company.game) or .obb files – target: /sdcard/Android/obb/<package>");
    private readonly CheckBox _downgrade = Ui.CheckBox("Downgrade erlauben (-d)", "Allow downgrade (-d)");
    private readonly CheckBox _grant = Ui.CheckBox("Alle Berechtigungen gewähren (-g)", "Grant all permissions (-g)");
    private readonly DarkButton _pickApk = Ui.Button("APK auswählen …", "Choose APK …");
    private readonly DarkButton _pickObb = Ui.Button("OBB-Ordner auswählen …", "Choose OBB folder …");
    private readonly DarkButton _restoreButton = Ui.Button("Batch-Restore …", "Batch restore …", ButtonKind.Normal);
    private readonly DarkButton _explorer = Ui.Button("Gerät im Explorer öffnen", "Open device in Explorer");
    private readonly DarkButton _cancel = Ui.Button("Abbrechen", "Cancel", ButtonKind.Danger);
    private readonly DarkProgressBar _progress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 26, Margin = new Padding(8, 4, 0, 4) };
    private readonly Label _status = Ui.Value("");
    private readonly TextBox _log = Ui.Console();

    private CancellationTokenSource? _cts;

    public InstallPage(AppState state)
    {
        _state = state;
        BackColor = Theme.Background;
        DoubleBuffered = true;
        Padding = new Padding(20, 16, 20, 16);

        BuildUi();

        _state.BusyChanged += UpdateEnabled;
        _state.SelectedDeviceChanged += UpdateEnabled;
        _state.AdbChanged += UpdateEnabled;
        Loc.LanguageChanged += UpdateEnabled;
        UpdateEnabled();
    }

    private void BuildUi()
    {
        // APK card
        var apkCard = new Card("APK installieren", "Install APK") { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(0, 0, 10, 12) };
        var apkBox = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Dock = DockStyle.Top };
        apkBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _apkZone.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _apkZone.Margin = new Padding(0, 0, 0, 8);
        _apkZone.Accepts = paths => paths.Any(p => Directory.Exists(p) || ApkBundle.IsInstallable(p));
        _apkZone.Dropped += async paths => await InstallAsync(paths);
        _apkZone.Click += (_, _) => PickApk();
        _pickApk.Click += (_, _) => PickApk();
        _downgrade.Checked = _state.Settings.InstallAllowDowngrade;
        _grant.Checked = _state.Settings.InstallGrantPermissions;
        _downgrade.CheckedChanged += (_, _) => { _state.Settings.InstallAllowDowngrade = _downgrade.Checked; _state.Settings.Save(); };
        _grant.CheckedChanged += (_, _) => { _state.Settings.InstallGrantPermissions = _grant.Checked; _state.Settings.Save(); };
        apkBox.Controls.Add(_apkZone, 0, 0);
        apkBox.Controls.Add(_downgrade, 0, 1);
        apkBox.Controls.Add(_grant, 0, 2);
        _restoreButton.Click += (_, _) => PickBackupFolder();
        var pickApkRow = Ui.Row(_pickApk, _restoreButton);
        pickApkRow.Margin = new Padding(0, 8, 0, 0);
        apkBox.Controls.Add(pickApkRow, 0, 3);
        var restoreHint = Ui.Label(
            "Batch-Restore: einen ganzen Backup-Ordner laden (oder auf das Feld ziehen) und auswählen, welche Apps mit APK, OBB und Daten in einem Rutsch wiederhergestellt werden.",
            "Batch restore: load a whole backup folder (or drop it onto the field) and choose which apps to restore in one go, with APK, OBB and data.", muted: true);
        restoreHint.Margin = new Padding(0, 6, 0, 0);
        Ui.WrapTo(restoreHint, apkCard);
        apkBox.Controls.Add(restoreHint, 0, 4);
        apkCard.SetContent(apkBox);

        // OBB card
        var obbCard = new Card("OBB kopieren", "Copy OBB") { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(10, 0, 0, 12) };
        var obbBox = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Dock = DockStyle.Top };
        obbBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _obbZone.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _obbZone.Margin = new Padding(0, 0, 0, 8);
        _obbZone.Accepts = paths => paths.Any(p => Directory.Exists(p) || p.EndsWith(".obb", StringComparison.OrdinalIgnoreCase));
        _obbZone.Dropped += async paths => await CopyObbAsync(paths);
        _obbZone.Click += (_, _) => PickObb();
        _pickObb.Click += (_, _) => PickObb();
        obbBox.Controls.Add(_obbZone, 0, 0);
        var obbHint = Ui.Label(
            "Der Paketname wird aus dem Ordnernamen, einem Backup-Ordner „App [Paket]“ oder den .obb-Dateinamen ermittelt; sonst wird nachgefragt.",
            "The package name is taken from the folder name, a backup folder \"App [package]\" or the .obb file names; otherwise you are asked.", muted: true);
        obbHint.Margin = new Padding(0, 4, 0, 0);
        Ui.WrapTo(obbHint, obbCard);
        obbBox.Controls.Add(obbHint, 0, 1);
        var pickObbRow = Ui.Row(_pickObb);
        pickObbRow.Margin = new Padding(0, 8, 0, 0);
        obbBox.Controls.Add(pickObbRow, 0, 2);
        obbCard.SetContent(obbBox);

        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(apkCard, 0, 0);
        root.Controls.Add(obbCard, 1, 0);

        // Actions: explorer, cancel, progress
        _explorer.Click += (_, _) => OpenInExplorer();
        _cancel.Click += (_, _) => CancelOperation();
        var actions = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.Controls.Add(_explorer, 0, 0);
        actions.Controls.Add(_cancel, 1, 0);
        actions.Controls.Add(_progress, 2, 0);
        _status.AutoEllipsis = true;
        _status.Margin = new Padding(0, 8, 0, 4);
        actions.Controls.Add(_status, 0, 1);
        actions.SetColumnSpan(_status, 3);
        var explorerHint = Ui.Label(
            "Explorer-Zugriff nutzt MTP: Am Headset ggf. „Dateiübertragung“ bzw. den USB-Zugriff auf Dateien erlauben.",
            "Explorer access uses MTP: on the headset allow \"File transfer\" / USB access to files if asked.", muted: true);
        explorerHint.Margin = new Padding(0, 0, 0, 10);
        Ui.WrapTo(explorerHint, this);
        actions.Controls.Add(explorerHint, 0, 2);
        actions.SetColumnSpan(explorerHint, 3);

        // Log
        var logCard = new Card("Protokoll", "Log") { Dock = DockStyle.Fill, Margin = new Padding(0) };
        Ui.Placeholder(_log, "Hier erscheinen Installations- und Kopiervorgänge.", "Install and copy operations appear here.");
        logCard.SetContent(_log, fill: true);

        Controls.Add(logCard);
        Controls.Add(actions);
        Controls.Add(root);

        bool narrowMode = false;
        void UpdateScroll()
        {
            AutoScrollMinSize = narrowMode ? new Size(0, root.Height + actions.Height + logCard.Height + Padding.Vertical) : Size.Empty;
        }
        Ui.Responsive(this, root, apkCard, obbCard, breakpoint: 900, changed: narrow =>
        {
            narrowMode = narrow;
            AutoScroll = narrow;
            logCard.Dock = narrow ? DockStyle.Top : DockStyle.Fill;
            if (narrow) logCard.Height = Theme.S(200);
            apkCard.Margin = narrow ? new Padding(0, 0, 0, Theme.S(12)) : new Padding(0, 0, Theme.S(10), Theme.S(12));
            obbCard.Margin = narrow ? new Padding(0, 0, 0, Theme.S(12)) : new Padding(Theme.S(10), 0, 0, Theme.S(12));
            UpdateScroll();
        });
        root.SizeChanged += (_, _) => UpdateScroll();
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
        _apkZone.Enabled = _obbZone.Enabled = _pickApk.Enabled = _pickObb.Enabled = _restoreButton.Enabled = ready;
        _explorer.Enabled = _state.SelectedDevice is not null;
        _cancel.Enabled = running && !_cts!.IsCancellationRequested;
        if (!running)
        {
            SetStatus(!_state.HasAdb
                ? Loc.T("ADB ist nicht eingerichtet (Tab „Allgemeine Einstellungen“).", "ADB is not set up (\"General settings\" tab).")
                : _state.SelectedDevice is not { IsReady: true } d
                    ? Loc.T("Kein bereites Gerät ausgewählt.", "No ready device selected.")
                    : !idle
                        ? Loc.T("Ein anderer ADB-Vorgang läuft gerade.", "Another ADB operation is running.")
                        : Loc.T($"Ziel: {d.DisplayName} ({d.Serial})", $"Target: {d.DisplayName} ({d.Serial})"));
        }
    }

    private void SetStatus(string text) => _status.Text = text;

    private void Log(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => Log(text))); return; }
        Ui.AppendLine(_log, text);
    }

    private void PickApk()
    {
        if (!_apkZone.Enabled) return;
        using var dialog = new OpenFileDialog
        {
            Title = Loc.T("APK auswählen", "Choose APK"),
            Filter = Loc.T("Android-Apps", "Android apps") + " (*.apk; *.apks; *.xapk; *.apkm)|*.apk;*.apks;*.xapk;*.apkm",
            Multiselect = true
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            _ = InstallAsync(dialog.FileNames);
    }

    private void PickObb()
    {
        if (!_obbZone.Enabled) return;
        using var dialog = new FolderBrowserDialog
        {
            Description = Loc.T("OBB-Ordner auswählen (Ordnername = Paketname)", "Choose OBB folder (folder name = package name)"),
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            _ = CopyObbAsync(new[] { dialog.SelectedPath });
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
            OpenRestore(paths[0]);
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
                _progress.Marquee = true;
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
                        string? package = job.ObbPackage ?? AskPackage(job.ObbFolder);
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
                MessageBox.Show(FindForm(), Loc.T($"{failed} Installation(en) fehlgeschlagen. Details stehen im Protokoll.",
                    $"{failed} installation(s) failed. See the log for details."), "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            Log(Loc.T("Abgebrochen.", "Cancelled."));
            SetStatus(Loc.T("Abgebrochen.", "Cancelled."));
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            MessageBox.Show(FindForm(), ex.Message, "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                    await PushTreeAsync(adb, folder, $"/sdcard/Android/obb/{package}", "OBB", token);
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
        if (_downgrade.Checked) args.Add("-d");
        if (_grant.Checked) args.Add("-g");
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

    private void PickBackupFolder()
    {
        if (!_restoreButton.Enabled) return;
        using var dialog = new FolderBrowserDialog
        {
            Description = Loc.T("Backup-Ordner auswählen (Backup_… oder der Ordner mit mehreren Backups)", "Choose backup folder (Backup_… or the folder containing several backups)"),
            UseDescriptionForTitle = true
        };
        if (Directory.Exists(_state.Settings.BackupDestination))
            dialog.InitialDirectory = _state.Settings.BackupDestination;
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            OpenRestore(dialog.SelectedPath);
    }

    private void OpenRestore(string folder)
    {
        AdbDevice? device = _state.SelectedDevice;
        if (!DeviceReady || device is null || _state.IsBusy)
            return;

        List<RestoreEntry> entries;
        Cursor = Cursors.WaitCursor;
        try { entries = BackupScanner.Scan(folder); }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(), ex.Message, "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor = Cursors.Default; }

        if (entries.Count == 0)
        {
            MessageBox.Show(FindForm(), Loc.T($"In diesem Ordner wurde kein App-Backup gefunden:\n{folder}\n\nErwartet werden App-Ordner „Name [Paket]“ mit APK/, OBB/ oder Data/.",
                    $"No app backup was found in this folder:\n{folder}\n\nExpected are app folders \"Name [package]\" containing APK/, OBB/ or Data/."),
                "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        List<RestoreEntry>? selected = RestoreDialog.Show(FindForm(), folder, entries, $"{device.DisplayName} ({device.Serial})");
        if (selected is { Count: > 0 })
            _ = RestoreAsync(selected);
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
        _progress.Marquee = false;
        _progress.Maximum = Math.Max(1, steps);
        _progress.Value = 0;
        var total = Stopwatch.StartNew();

        Log("");
        Log(Loc.T($"══ Batch-Restore: {items.Count} App(s) → {device.DisplayName} ({device.Serial})",
                  $"══ Batch restore: {items.Count} app(s) → {device.DisplayName} ({device.Serial})"));

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
                        await PushTreeAsync(adb, item.ObbDir, $"/sdcard/Android/obb/{item.Package}", "OBB", token);
                        _progress.Value = ++step;
                    }
                    if (item.Data && item.DataDir is not null)
                    {
                        SetStatus(Loc.T($"Kopiere Daten: {label} …", $"Copying data: {label} …"));
                        await adb.CaptureAsync(token, "shell", AdbClient.ShellJoin(new[] { "am", "force-stop", item.Package }));
                        await PushTreeAsync(adb, item.DataDir, $"/sdcard/Android/data/{item.Package}", Loc.T("Daten", "Data"), token, viaStaging: true);
                        _progress.Value = ++step;
                    }
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
                MessageBox.Show(FindForm(), Loc.T($"Bei {failed.Count} App(s) ist ein Fehler aufgetreten:\n\n", $"{failed.Count} app(s) failed:\n\n") +
                    string.Join("\n", failed.Take(15)) + (failed.Count > 15 ? "\n…" : "") +
                    Loc.T("\n\nDetails stehen im Protokoll.", "\n\nSee the log for details."), "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            Log(Loc.T("Abgebrochen – die aktuelle App ist ggf. nur teilweise wiederhergestellt.", "Cancelled – the current app may be restored only partially."));
            SetStatus(Loc.T("Abgebrochen.", "Cancelled."));
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            MessageBox.Show(FindForm(), ex.Message, "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Finish();
        }
    }

    private const string StagingDir = "/data/local/tmp/adbora-restore";

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

    private string? AskPackage(string folder)
    {
        using Form dialog = Ui.Dialog(Loc.T("Paketname angeben", "Enter package name"), 560, 190);
        dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
        var text = new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.S(46),
            Text = Loc.T($"Für „{Path.GetFileName(folder)}“ konnte kein Paketname ermittelt werden.\nPaketname der App (z. B. com.firma.spiel):",
                         $"No package name could be determined for \"{Path.GetFileName(folder)}\".\nPackage name of the app (e.g. com.company.game):")
        };
        var box = Ui.TextBox(Theme.S(480));
        box.Dock = DockStyle.Top;
        var ok = Ui.Button("OK", "OK", ButtonKind.Primary);
        var cancel = Ui.Button("Abbrechen", "Cancel");
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.Transparent };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        dialog.Controls.Add(box);
        dialog.Controls.Add(text);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        while (Ui.ShowDialog(dialog, FindForm()) == DialogResult.OK)
        {
            string value = box.Text.Trim();
            if (PackageRe.IsMatch(value)) return value;
            MessageBox.Show(FindForm(), Loc.T("Ungültiger Paketname.", "Invalid package name."), "ADBora");
        }
        return null;
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
            string? package = PackageForFolder(path) ?? AskPackage(path);
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
            string? package = PackageRe.IsMatch(group.Key) ? group.Key : AskPackage(group.First());
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
            MessageBox.Show(FindForm(), ex.Message, "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        _progress.Marquee = false;
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
        string result = _status.Text; // keep the result message visible
        _cts?.Dispose();
        _cts = null;
        _progress.Marquee = false;
        _state.EndOperation();
        UpdateEnabled();
        _status.Text = result;
    }

    // ------------------------------------------------------------------
    // Explorer (MTP)
    // ------------------------------------------------------------------

    private void OpenInExplorer()
    {
        AdbDevice? device = _state.SelectedDevice;
        string? path = device is null ? null : FindShellDevice(device);
        try
        {
            if (path is not null)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                Log(Loc.T($"Explorer geöffnet: {device!.DisplayName}", $"Explorer opened: {device!.DisplayName}"));
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:MyComputerFolder") { UseShellExecute = true });
                Log(Loc.T("Gerät nicht unter „Dieser PC“ gefunden – „Dieser PC“ wurde geöffnet. Am Headset ggf. den Dateizugriff (MTP) erlauben.",
                          "Device not found under \"This PC\" – opened \"This PC\". Allow file access (MTP) on the headset if asked."));
            }
        }
        catch (Exception ex)
        {
            Log(ex.Message);
        }
    }

    /// <summary>Looks for the device (MTP) in the "This PC" shell folder by its model name.</summary>
    private static string? FindShellDevice(AdbDevice device)
    {
        try
        {
            Type? type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic? computer = shell.NameSpace(17); // ssfDRIVES = This PC
            if (computer is null) return null;

            string[] names = new[] { device.Model, device.DisplayName, device.Product, device.Device }
                .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();
            string? fallback = null;
            foreach (dynamic item in computer.Items())
            {
                string name = (string)item.Name;
                string path = (string)item.Path;
                if (!path.Contains("usb#", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("::", StringComparison.Ordinal))
                    continue; // normal drives
                if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    return path;
                if (names.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase) || n.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    fallback ??= path;
            }
            return fallback;
        }
        catch
        {
            return null;
        }
    }
}
