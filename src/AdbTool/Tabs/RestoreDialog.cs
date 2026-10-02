using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AdbTool.Backup;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>One app folder of an ADBora / APK Backup Tool backup.</summary>
internal sealed class RestoreEntry
{
    public string Folder { get; init; } = "";
    public string Run { get; init; } = "";
    public string Package { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public List<string> Apks { get; } = new();
    public string? ObbDir { get; set; }
    public string? DataDir { get; set; }
    public long ApkBytes { get; set; }
    public long ObbBytes { get; set; }
    public long DataBytes { get; set; }

    public bool Apk { get; set; }
    public bool Obb { get; set; }
    public bool Data { get; set; }

    public bool HasApk => Apks.Count > 0;
    public bool HasObb => ObbDir is not null;
    public bool HasData => DataDir is not null;
    public bool AnySelected => Apk || Obb || Data;
    public long SelectedBytes => (Apk ? ApkBytes : 0) + (Obb ? ObbBytes : 0) + (Data ? DataBytes : 0);
}

/// <summary>Finds app folders ("Name [package]" with APK/, OBB/, Data/) in a backup.</summary>
internal static class BackupScanner
{
    private static readonly Regex BracketPackage = new(@"\[([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?:-[0-9a-f]+)?\]", RegexOptions.Compiled);
    private static readonly Regex PackageRe = new(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$", RegexOptions.Compiled);

    public static bool IsAppFolder(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "manifest.json"))) return true;
            string apk = Path.Combine(dir, "APK");
            return Directory.Exists(apk) && Directory.EnumerateFiles(apk).Any(ApkBundle.IsInstallable);
        }
        catch { return false; }
    }

    /// <summary>A backup run (Backup_…) or a folder containing several runs.</summary>
    public static bool ContainsAppFolders(string dir)
    {
        try
        {
            if (IsAppFolder(dir)) return false;
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                if (IsAppFolder(sub)) return true;
                if (Directory.EnumerateDirectories(sub).Any(IsAppFolder)) return true;
            }
        }
        catch { }
        return false;
    }

    public static List<RestoreEntry> Scan(string path)
    {
        var folders = new List<(string Folder, string Run)>();
        if (IsAppFolder(path))
        {
            folders.Add((path, Path.GetFileName(Path.GetDirectoryName(path)) ?? ""));
        }
        else
        {
            foreach (string sub in Directory.EnumerateDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (IsAppFolder(sub))
                {
                    folders.Add((sub, Path.GetFileName(path)));
                    continue;
                }
                // Folder with several backup runs (e.g. "APK-Backups")
                foreach (string app in Directory.EnumerateDirectories(sub).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    if (IsAppFolder(app))
                        folders.Add((app, Path.GetFileName(sub)));
            }
        }

        var entries = new List<RestoreEntry>();
        foreach (var (folder, run) in folders)
        {
            try
            {
                RestoreEntry? e = Read(folder, run);
                if (e is not null && (e.HasApk || e.HasObb || e.HasData))
                    entries.Add(e);
            }
            catch { }
        }
        return entries
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Run, StringComparer.Ordinal)
            .ToList();
    }

    private static RestoreEntry? Read(string folder, string run)
    {
        var entry = new RestoreEntry { Folder = folder, Run = run };
        JsonNode? manifest = null;
        string manifestPath = Path.Combine(folder, "manifest.json");
        try { if (File.Exists(manifestPath)) manifest = JsonNode.Parse(File.ReadAllText(manifestPath)); } catch { }

        string folderName = Path.GetFileName(folder);
        entry.Package = manifest?["package"]?.GetValue<string>() ?? "";
        if (!PackageRe.IsMatch(entry.Package))
            entry.Package = BracketPackage.Match(folderName) is { Success: true } m ? m.Groups[1].Value : "";
        entry.Name = manifest?["name"]?.GetValue<string>() is { Length: > 0 } n ? n
            : folderName.Contains(" [") ? folderName[..folderName.IndexOf(" [", StringComparison.Ordinal)] : folderName;
        entry.Version = manifest?["version"]?.GetValue<string>() ?? "";

        // APKs: the file list of the manifest keeps base + splits together
        string apkDir = Path.Combine(folder, "APK");
        if (manifest?["apk_files"] is JsonArray files && files.Count > 0)
        {
            foreach (JsonNode? f in files)
            {
                string? rel = f?.GetValue<string>();
                if (rel is null) continue;
                string full = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) entry.Apks.Add(full);
            }
            if (entry.Apks.Count != files.Count) entry.Apks.Clear(); // incomplete: fall back below
        }
        if (entry.Apks.Count == 0 && Directory.Exists(apkDir))
            entry.Apks.AddRange(InstallFiles(apkDir));
        if (entry.Apks.Count == 0) // older backup layout: APKs directly in the app folder
            entry.Apks.AddRange(InstallFiles(folder));
        entry.ApkBytes = entry.Apks.Sum(f => new FileInfo(f).Length);

        if (entry.Package.Length == 0 && entry.Apks.Count > 0)
        {
            try { entry.Package = ApkMetadataReader.ReadPackage(entry.Apks[0]).Package; } catch { }
        }
        if (entry.Package.Length == 0)
            return null;

        (entry.ObbDir, entry.ObbBytes) = Payload(Path.Combine(folder, "OBB"));
        (entry.DataDir, entry.DataBytes) = Payload(Path.Combine(folder, "Data"));

        entry.Apk = entry.HasApk;
        entry.Obb = entry.HasObb;
        entry.Data = entry.HasData;
        return entry;
    }

    /// <summary>Single/split APKs, or – if there are none – one .apks/.xapk/.apkm bundle.</summary>
    private static IEnumerable<string> InstallFiles(string dir)
    {
        var apks = Directory.GetFiles(dir, "*.apk").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (apks.Count > 0) return apks;
        return Directory.GetFiles(dir).Where(ApkBundle.IsBundle).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(1);
    }

    private static (string?, long) Payload(string dir)
    {
        if (!Directory.Exists(dir)) return (null, 0);
        long bytes = 0;
        int count = 0;
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            count++;
            bytes += new FileInfo(f).Length;
        }
        return count > 0 ? (dir, bytes) : (null, 0);
    }
}

/// <summary>Selection dialog: which apps and parts (APK / OBB / Data) to restore.</summary>
internal static class RestoreDialog
{
    private const int ColApp = 0, ColApk = 1, ColObb = 2, ColData = 3, ColSize = 4;

    public static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB"
        : bytes >= 1024L * 1024 || bytes == 0
            ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
            : $"{Math.Max(1, bytes / 1024.0):0} KB";

    public static List<RestoreEntry>? Show(IWin32Window? owner, string path, List<RestoreEntry> entries, string deviceName)
    {
        using Form dialog = Ui.Dialog(Loc.T("Batch-Restore", "Batch restore"), 900, 620);
        dialog.MinimumSize = new Size(Theme.S(620), Theme.S(420));

        var title = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = Theme.S(30),
            Font = Theme.SectionFont,
            Text = Loc.T($"Wiederherstellen auf {deviceName}", $"Restore to {deviceName}")
        };
        var source = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            AutoEllipsis = true,
            Height = Theme.S(26),
            ForeColor = Theme.Muted,
            UseMnemonic = false,
            Text = Loc.T($"{entries.Count} App(s) in: {path}", $"{entries.Count} app(s) in: {path}")
        };

        var grid = new DataGridView();
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardBorder, Padding = new Padding(1) };
        grid.Dock = DockStyle.Fill;
        host.Controls.Add(grid);
        StyleGrid(grid);

        var summary = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = Theme.S(34), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft };
        var note = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = Theme.S(40),
            ForeColor = Theme.Muted,
            Text = Loc.T("Reihenfolge je App: APK installieren → OBB kopieren → Daten kopieren (die App wird dafür beendet). Vorhandene Dateien werden überschrieben. Klick auf eine Spaltenüberschrift wählt die ganze Spalte.",
                         "Order per app: install APK → copy OBB → copy data (the app is stopped first). Existing files are overwritten. Click a column header to toggle the whole column.")
        };

        var restore = Ui.Button("Wiederherstellen", "Restore", ButtonKind.Primary);
        var cancel = Ui.Button("Abbrechen", "Cancel");
        var all = Ui.Button("Alles auswählen", "Select all");
        var none = Ui.Button("Nichts auswählen", "Select none");
        restore.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Color.Transparent };
        cancel.Margin = new Padding(0);
        right.Controls.Add(cancel);
        right.Controls.Add(restore);
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
        left.Controls.Add(all);
        left.Controls.Add(none);
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(46), Padding = new Padding(0, Theme.S(10), 0, 0), BackColor = Color.Transparent };
        buttons.Controls.Add(left);
        buttons.Controls.Add(right);

        dialog.Controls.Add(host);
        dialog.Controls.Add(source);
        dialog.Controls.Add(title);
        dialog.Controls.Add(note);
        dialog.Controls.Add(summary);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = restore;
        dialog.CancelButton = cancel;

        foreach (RestoreEntry e in entries)
        {
            int row = grid.Rows.Add("", "", "", "", "");
            grid.Rows[row].Tag = e;
        }

        void UpdateSummary()
        {
            var selected = entries.Where(e => e.AnySelected).ToList();
            summary.Text = Loc.T(
                $"{selected.Count} von {entries.Count} Apps ausgewählt · APK {selected.Count(e => e.Apk)} · OBB {selected.Count(e => e.Obb)} · Daten {selected.Count(e => e.Data)} · {FormatSize(selected.Sum(e => e.SelectedBytes))}",
                $"{selected.Count} of {entries.Count} apps selected · APK {selected.Count(e => e.Apk)} · OBB {selected.Count(e => e.Obb)} · Data {selected.Count(e => e.Data)} · {FormatSize(selected.Sum(e => e.SelectedBytes))}");
            restore.Enabled = selected.Count > 0;
            grid.Invalidate();
        }

        void Toggle(RestoreEntry e, int column)
        {
            switch (column)
            {
                case ColApk when e.HasApk: e.Apk = !e.Apk; break;
                case ColObb when e.HasObb: e.Obb = !e.Obb; break;
                case ColData when e.HasData: e.Data = !e.Data; break;
                case ColApp:
                    bool any = e.AnySelected;
                    e.Apk = !any && e.HasApk;
                    e.Obb = !any && e.HasObb;
                    e.Data = !any && e.HasData;
                    break;
            }
        }

        void SetColumn(int column)
        {
            bool Has(RestoreEntry e) => column switch { ColApk => e.HasApk, ColObb => e.HasObb, _ => e.HasData };
            bool Get(RestoreEntry e) => column switch { ColApk => e.Apk, ColObb => e.Obb, _ => e.Data };
            bool value = !entries.Where(Has).All(Get);
            foreach (RestoreEntry e in entries.Where(Has))
            {
                if (column == ColApk) e.Apk = value;
                else if (column == ColObb) e.Obb = value;
                else e.Data = value;
            }
        }

        grid.MouseDown += (_, ev) =>
        {
            if (ev.Button != MouseButtons.Left) return;
            DataGridView.HitTestInfo hit = grid.HitTest(ev.X, ev.Y);
            if (hit.Type == DataGridViewHitTestType.ColumnHeader && hit.ColumnIndex is ColApk or ColObb or ColData)
                SetColumn(hit.ColumnIndex);
            else if (hit.Type == DataGridViewHitTestType.Cell && grid.Rows[hit.RowIndex].Tag is RestoreEntry e)
                Toggle(e, hit.ColumnIndex);
            else
                return;
            UpdateSummary();
        };
        grid.KeyDown += (_, ev) =>
        {
            if (ev.KeyCode == Keys.Space && grid.CurrentCell is { } cell && grid.Rows[cell.RowIndex].Tag is RestoreEntry e)
            {
                Toggle(e, cell.ColumnIndex);
                UpdateSummary();
                ev.Handled = true;
            }
        };
        grid.CellPainting += (_, ev) => Paint(grid, ev);
        all.Click += (_, _) =>
        {
            foreach (RestoreEntry e in entries) { e.Apk = e.HasApk; e.Obb = e.HasObb; e.Data = e.HasData; }
            UpdateSummary();
        };
        none.Click += (_, _) =>
        {
            foreach (RestoreEntry e in entries) { e.Apk = e.Obb = e.Data = false; }
            UpdateSummary();
        };

        UpdateSummary();
        return Ui.ShowDialog(dialog, owner) == DialogResult.OK ? entries.Where(e => e.AnySelected).ToList() : null;
    }

    private static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Theme.Card;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = Theme.Grid;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.TableHeader, ForeColor = Theme.Text, Font = Theme.BoldFont,
            SelectionBackColor = Theme.TableHeader, SelectionForeColor = Theme.Text, Padding = new Padding(6, 0, 6, 0)
        };
        grid.ColumnHeadersHeight = Theme.S(36);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.BaseFont,
            SelectionBackColor = Theme.Selection, SelectionForeColor = Theme.Text, Padding = new Padding(4, 0, 4, 0)
        };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.TableAlt };
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.RowTemplate.Height = Theme.S(48);
        grid.ReadOnly = true;
        Theme.UseDarkScrollBars(grid);
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(grid, true);

        grid.Columns.Add(new DataGridViewTextBoxColumn { AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = Theme.S(200) });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(64), HeaderText = "APK", Resizable = DataGridViewTriState.False });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(64), HeaderText = "OBB", Resizable = DataGridViewTriState.False });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(64), Resizable = DataGridViewTriState.False });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(96), Resizable = DataGridViewTriState.False });
        foreach (DataGridViewColumn c in grid.Columns)
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
        for (int c = ColApk; c <= ColData; c++)
            grid.Columns[c].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.Columns[ColSize].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        grid.Columns[ColApp].HeaderText = Loc.T("App / Paket", "App / package");
        grid.Columns[ColData].HeaderText = Loc.T("Daten", "Data");
        grid.Columns[ColSize].HeaderText = Loc.T("Größe", "Size");
    }

    private static void Paint(DataGridView grid, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics is null || grid.Rows[e.RowIndex].Tag is not RestoreEntry entry)
            return;
        Graphics g = e.Graphics;
        bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
        e.PaintBackground(e.CellBounds, selected);
        Color dim = entry.AnySelected ? Theme.Text : Theme.DisabledText;

        switch (e.ColumnIndex)
        {
            case ColApp:
            {
                int h = e.CellBounds.Height;
                string title = entry.Name + (entry.Version.Length > 0 ? "  ·  " + entry.Version : "");
                string sub = entry.Package + (entry.Run.Length > 0 ? "  ·  " + entry.Run : "");
                var titleRect = new Rectangle(e.CellBounds.X + Theme.S(8), e.CellBounds.Y + h / 2 - Theme.S(21), e.CellBounds.Width - Theme.S(14), Theme.S(21));
                var subRect = new Rectangle(e.CellBounds.X + Theme.S(8), e.CellBounds.Y + h / 2, e.CellBounds.Width - Theme.S(14), Theme.S(19));
                TextRenderer.DrawText(g, title, Theme.BoldFont, titleRect, dim, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, sub, Theme.BaseFont, subRect, Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                break;
            }
            case ColApk or ColObb or ColData:
            {
                bool has = e.ColumnIndex switch { ColApk => entry.HasApk, ColObb => entry.HasObb, _ => entry.HasData };
                bool on = e.ColumnIndex switch { ColApk => entry.Apk, ColObb => entry.Obb, _ => entry.Data };
                if (!has)
                {
                    TextRenderer.DrawText(g, "–", Theme.BaseFont, e.CellBounds, Theme.DisabledText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    break;
                }
                int bs = Theme.S(20);
                var box = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - bs) / 2, e.CellBounds.Y + (e.CellBounds.Height - bs) / 2, bs, bs);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Theme.RoundedRect(box, Theme.S(4)))
                using (var fill = new SolidBrush(on ? Theme.Primary : Theme.Input))
                using (var pen = new Pen(on ? Theme.PrimaryBorder : Theme.ButtonBorder, 1.4f))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }
                if (on)
                {
                    using var tick = new Pen(Color.White, Theme.S(2.2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    float k = bs / 20f;
                    g.DrawLines(tick, new[] { new PointF(box.X + 5 * k, box.Y + 10.5f * k), new PointF(box.X + 8.5f * k, box.Y + 14 * k), new PointF(box.X + 15 * k, box.Y + 6.5f * k) });
                }
                break;
            }
            case ColSize:
            {
                var r = new Rectangle(e.CellBounds.X, e.CellBounds.Y, e.CellBounds.Width - Theme.S(10), e.CellBounds.Height);
                TextRenderer.DrawText(g, FormatSize(entry.SelectedBytes), Theme.BaseFont, r, entry.AnySelected ? Theme.Text : Theme.DisabledText,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                break;
            }
        }
        e.Handled = true;
    }
}
