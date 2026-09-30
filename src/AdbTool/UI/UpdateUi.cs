using System.Diagnostics;
using AdbTool.Core;

namespace AdbTool.UI;

/// <summary>Update check (at start and on demand) and the update dialog.</summary>
internal static class UpdateUi
{
    private static bool _checking;

    public static string Status { get; private set; } = "";
    public static ReleaseInfo? Latest { get; private set; }

    public static event Action? StatusChanged;

    private static void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Checks GitHub for a newer version. <paramref name="manual"/> = started by
    /// the user: results and errors are always shown; otherwise only a new,
    /// not skipped version opens the dialog.
    /// </summary>
    public static async Task CheckAsync(Form owner, AppState state, Action exitApp, bool manual)
    {
        if (_checking) return;
        _checking = true;
        SetStatus(Loc.T("Suche nach Updates …", "Checking for updates …"));
        try
        {
            ReleaseInfo release = await Updater.GetLatestAsync(CancellationToken.None);
            Latest = release;
            if (!Updater.IsNewer(release))
            {
                SetStatus(Loc.T($"ADBora ist aktuell (neueste Version: {release.VersionText}).", $"ADBora is up to date (latest version: {release.VersionText})."));
                if (manual)
                    MessageBox.Show(owner, Loc.T($"Du verwendest die aktuelle Version ({Updater.CurrentVersion.ToString(3)}).", $"You are using the latest version ({Updater.CurrentVersion.ToString(3)})."),
                        "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetStatus(Loc.T($"Update verfügbar: Version {release.VersionText}", $"Update available: version {release.VersionText}"));
            if (!manual && state.Settings.SkippedUpdateVersion == release.VersionText)
                return;
            if (owner.IsDisposed || !owner.Visible) return;
            ShowDialog(owner, state, release, exitApp);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("Update-Prüfung fehlgeschlagen: ", "Update check failed: ") + ex.Message);
            if (manual)
                MessageBox.Show(owner, Loc.T("Die Update-Prüfung ist fehlgeschlagen:\n", "The update check failed:\n") + ex.Message,
                    "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Very small Markdown → plain text conversion for the release notes.</summary>
    private static string PlainText(string markdown)
    {
        var lines = new List<string>();
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            string t = line.Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\|?\s*:?-{2,}")) continue; // table separator
            if (t.StartsWith('#')) line = t.TrimStart('#').Trim().ToUpperInvariant();
            if (t.StartsWith('|'))
                line = string.Join("  ·  ", t.Trim('|').Split('|').Select(c => c.Trim()).Where(c => c.Length > 0));
            if (t.StartsWith("- ") || t.StartsWith("* ")) line = "• " + t[2..];
            line = line.Replace("**", "").Replace("`", "");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]+)\]\(([^)]+)\)", "$1 ($2)");
            lines.Add(line);
        }
        return string.Join(Environment.NewLine, lines).Trim();
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static void ShowDialog(Form owner, AppState state, ReleaseInfo release, Action exitApp)
    {
        ReleaseAsset? asset = Updater.PackageFor(release);
        bool automatic = asset is not null && (Updater.Kind != InstallKind.Portable || Updater.CanWriteProgramFolder());

        using Form dialog = Ui.Dialog(Loc.T("Update verfügbar", "Update available"), 660, 480);

        var title = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = Theme.S(34),
            Font = Theme.SectionFont,
            ForeColor = Theme.Accent,
            Text = Loc.T($"ADBora {release.VersionText} ist verfügbar", $"ADBora {release.VersionText} is available")
        };
        string package = asset is null ? "" : $" · {asset.Name} ({asset.Size / 1024.0 / 1024.0:0.0} MB)";
        string published = release.Published is { } p ? p.ToString("yyyy-MM-dd") : "-";
        var info = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = Theme.S(44),
            ForeColor = Theme.Muted,
            Text = Loc.T($"Installiert: {Updater.CurrentVersion.ToString(3)} ({Updater.KindText}) · Veröffentlicht: {published}{package}",
                         $"Installed: {Updater.CurrentVersion.ToString(3)} ({Updater.KindText}) · Published: {published}{package}") +
                   (automatic ? "" : "\n" + (asset is null
                       ? Loc.T("Automatisches Update ist für diese Kopie nicht möglich – die Download-Seite wird geöffnet.", "Automatic update is not possible for this copy – the download page will be opened.")
                       : Loc.T("Der Programmordner ist schreibgeschützt – die Download-Seite wird geöffnet.", "The program folder is read-only – the download page will be opened.")))
        };
        var notes = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Theme.Console,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.BaseFont,
            Text = PlainText(release.Notes.Length > 0 ? release.Notes : release.Title)
        };
        Theme.UseDarkScrollBars(notes);

        var progress = new DarkProgressBar { Dock = DockStyle.Bottom, Height = Theme.S(24), Visible = false, Maximum = 1000 };
        var status = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = Theme.S(30), ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };

        var update = Ui.Button(automatic ? "Jetzt aktualisieren" : "Download-Seite öffnen", automatic ? "Update now" : "Open download page", ButtonKind.Primary);
        var later = Ui.Button("Später", "Later");
        var skip = Ui.Button("Diese Version überspringen", "Skip this version");
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Color.Transparent,
            Padding = new Padding(0, Theme.S(10), 0, 0)
        };
        later.Margin = new Padding(0);
        buttons.Controls.Add(later);
        buttons.Controls.Add(update);
        buttons.Controls.Add(skip);

        dialog.Controls.Add(notes);
        dialog.Controls.Add(info);
        dialog.Controls.Add(title);
        dialog.Controls.Add(status);
        dialog.Controls.Add(progress);
        dialog.Controls.Add(buttons);
        dialog.CancelButton = later;

        CancellationTokenSource? cts = null;
        bool installing = false;

        later.Click += (_, _) =>
        {
            if (cts is not null) { cts.Cancel(); return; }
            dialog.Close();
        };
        skip.Click += (_, _) =>
        {
            state.Settings.SkippedUpdateVersion = release.VersionText;
            state.Settings.Save();
            dialog.Close();
        };
        dialog.FormClosing += (_, e) =>
        {
            if (installing) return;
            if (cts is not null) { cts.Cancel(); e.Cancel = true; }
        };

        update.Click += async (_, _) =>
        {
            if (!automatic || asset is null)
            {
                OpenUrl(release.PageUrl);
                dialog.Close();
                return;
            }
            if (state.IsBusy)
            {
                MessageBox.Show(dialog, Loc.T($"Bitte zuerst den laufenden Vorgang abschließen: {state.BusyOperation}", $"Please finish the running operation first: {state.BusyOperation}"),
                    "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            cts = new CancellationTokenSource();
            update.Enabled = skip.Enabled = false;
            later.Text = Loc.T("Abbrechen", "Cancel");
            progress.Visible = true;
            status.Text = Loc.T("Update wird heruntergeladen …", "Downloading update …");
            var report = new Progress<(long Done, long Total)>(p =>
            {
                progress.Value = p.Total > 0 ? (int)(p.Done * 1000 / p.Total) : 0;
                status.Text = Loc.T($"Update wird heruntergeladen … {p.Done / 1024.0 / 1024.0:0.0} / {p.Total / 1024.0 / 1024.0:0.0} MB",
                                    $"Downloading update … {p.Done / 1024.0 / 1024.0:0.0} / {p.Total / 1024.0 / 1024.0:0.0} MB");
            });
            try
            {
                string file = await Updater.DownloadAsync(asset, report, cts.Token);
                status.Text = Loc.T("Update wird installiert – ADBora startet danach neu …", "Installing update – ADBora restarts afterwards …");
                status.Refresh();
                Updater.StartInstall(file);
                installing = true;
                state.Settings.SkippedUpdateVersion = "";
                state.Settings.Save();
                dialog.Close();
            }
            catch (OperationCanceledException)
            {
                status.Text = Loc.T("Download abgebrochen.", "Download cancelled.");
                ResetButtons();
            }
            catch (Exception ex)
            {
                status.Text = Loc.T("Update fehlgeschlagen: ", "Update failed: ") + ex.Message;
                ResetButtons();
                if (MessageBox.Show(dialog, Loc.T($"Das Update ist fehlgeschlagen:\n{ex.Message}\n\nDownload-Seite öffnen?", $"The update failed:\n{ex.Message}\n\nOpen the download page?"),
                        "ADBora", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    OpenUrl(release.PageUrl);
            }

            void ResetButtons()
            {
                cts?.Dispose();
                cts = null;
                progress.Visible = false;
                update.Enabled = skip.Enabled = true;
                later.Text = Loc.T("Später", "Later");
            }
        };

        dialog.Shown += (_, _) =>
        {
            notes.SelectionStart = 0;
            notes.SelectionLength = 0;
            dialog.ActiveControl = update;
        };
        Ui.ShowDialog(dialog, owner);
        if (installing)
            exitApp();
    }
}
