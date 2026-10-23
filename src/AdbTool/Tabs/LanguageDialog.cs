using System.Diagnostics;
using System.Drawing.Drawing2D;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>Choose the UI language and add / remove additional languages (GitHub repository, folder "lang").</summary>
internal static class LanguageDialog
{
    private const int ColActive = 0, ColName = 1, ColVersion = 2, ColStatus = 3, ColCheck = 4, ColInstalled = 5;

    private sealed class Row
    {
        public string Code = "";
        public string Name = "";
        public bool BuiltIn;
        public LanguageFile? Local;
        public OnlineLanguage? Online;
        public BrokenLanguage? Broken;   // unreadable file: shown with the reason, not selectable
        public bool Installed;   // checkbox state
        public bool Newer => Online is not null && (Local is null || Languages.ParseVersion(Online.Version) > Languages.ParseVersion(Local.Version));
    }

    /// <summary>Returns the chosen language code, or null if cancelled.</summary>
    public static string? Show(IWin32Window? owner)
    {
        var rows = new List<Row>
        {
            new() { Code = "de", Name = "Deutsch", BuiltIn = true, Installed = true },
            new() { Code = "en", Name = "English", BuiltIn = true, Installed = true },
        };
        List<LanguageFile> installed = Languages.Installed(out List<BrokenLanguage> brokenLocal);
        foreach (LanguageFile l in installed)
            rows.Add(new Row { Code = l.Code, Name = l.Name, Local = l, Installed = true });
        foreach (BrokenLanguage b in brokenLocal)
            rows.Add(new Row { Code = b.Code, Name = b.FileName, Broken = b });
        string active = Loc.Language;

        using Form dialog = Ui.Dialog(Loc.T("Sprache", "Language"), 860, 560);
        var info = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = Theme.S(58), ForeColor = Theme.Muted,
            Text = Loc.T("Sprache wählen (Kreis). Deutsch und Englisch sind eingebaut; weitere Sprachen lassen sich aus dem GitHub-Repository herunterladen – Haken = installieren / aktualisieren, kein Haken = entfernen.",
                         "Choose the language (circle). German and English are built in; more languages can be downloaded from the GitHub repository – tick = install / update, no tick = remove.")
        };
        var status = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = Theme.S(28), ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft,
            Text = Loc.T("Online verfügbare Sprachen werden geladen …", "Loading languages available online …") };

        var grid = new DataGridView();
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardBorder, Padding = new Padding(1) };
        grid.Dock = DockStyle.Fill;
        host.Controls.Add(grid);
        grid.BackgroundColor = Theme.Card;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = Theme.Grid;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.TableHeader, ForeColor = Theme.Text, Font = Theme.BoldFont, SelectionBackColor = Theme.TableHeader, SelectionForeColor = Theme.Text };
        grid.ColumnHeadersHeight = Theme.S(34);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.BaseFont, SelectionBackColor = Theme.Selection, SelectionForeColor = Theme.Text };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.TableAlt };
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.ReadOnly = true;
        grid.RowTemplate.Height = Theme.S(32);
        Theme.UseDarkScrollBars(grid);
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(grid, true);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(60), HeaderText = Loc.T("Aktiv", "Active") });
        grid.Columns.Add(new DataGridViewTextBoxColumn { AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, HeaderText = Loc.T("Sprache", "Language") });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(80), HeaderText = "Version" });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(190), HeaderText = "Status" });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(150), HeaderText = Loc.T("Übersetzt", "Translated") });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(90), HeaderText = Loc.T("Installiert", "Installed") });
        foreach (DataGridViewColumn c in grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
        grid.Columns[ColActive].HeaderCell.Style.Alignment = grid.Columns[ColInstalled].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;

        bool Locked(Row r) => r.BuiltIn || r.Code == Loc.Language || r.Local is { Bundled: true };
        string StatusText(Row r)
        {
            if (r.Broken is { } b) return Loc.T("Fehlerhaft: ", "Broken: ") + b.Error;
            if (r.BuiltIn) return Loc.T("Eingebaut", "Built in");
            if (r.Local is { Bundled: true }) return Loc.T("Mitgeliefert", "Bundled");
            if (r.Local is not null && r.Newer) return Loc.T($"Update auf {r.Online!.Version}", $"Update to {r.Online!.Version}");
            if (r.Local is not null) return Loc.T("Installiert", "Installed");
            return Loc.T("Online verfügbar", "Available online");
        }
        void Fill()
        {
            grid.Rows.Clear();
            foreach (Row r in rows)
            {
                string label = r.Broken is { } b ? $"{b.FileName}{(b.Online ? " (GitHub)" : "")}" : $"{r.Name} ({r.Code})";
                // Check: translated texts and placeholder problems (local file, otherwise the online one)
                string check = "";
                string tip = "";
                LanguageFile? file = r.Local ?? (r.Online is { } o ? Languages.Parse(o.Content, o.Code) : null);
                if (r.BuiltIn) check = "100 %";
                else if (file is not null)
                {
                    Languages.CheckResult c = Languages.Check(file);
                    check = c.Total > 0 ? $"{c.Translated} / {c.Total}" : "–";
                    if (c.Problems.Count > 0)
                    {
                        check += Loc.T($" · {c.Problems.Count} Fehler", $" · {c.Problems.Count} errors");
                        tip = string.Join("\n", c.Problems.Take(15)) + (c.Problems.Count > 15 ? "\n…" : "");
                    }
                    else if (c.Total > 0 && c.Translated < c.Total)
                        tip = Loc.T($"{c.Total - c.Translated} Texte fehlen – sie erscheinen auf Englisch.", $"{c.Total - c.Translated} texts missing – they appear in English.");
                }
                int i = grid.Rows.Add("", label, r.Local?.Version ?? r.Online?.Version ?? (r.BuiltIn ? AppInfo.Version : ""), StatusText(r), check, "");
                grid.Rows[i].Tag = r;
                if (tip.Length > 0)
                {
                    grid.Rows[i].Cells[ColCheck].ToolTipText = tip;
                    if (tip.Contains('\n') || check.Contains('·')) grid.Rows[i].Cells[ColCheck].Style.ForeColor = Theme.Danger;
                }
                if (r.Broken is not null)
                {
                    grid.Rows[i].DefaultCellStyle.ForeColor = Theme.Danger;
                    grid.Rows[i].Cells[ColStatus].ToolTipText = StatusText(r);
                }
            }
        }
        Fill();

        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.Graphics is null || grid.Rows[e.RowIndex].Tag is not Row r) return;
            if (e.ColumnIndex != ColActive && e.ColumnIndex != ColInstalled) return;
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            if (r.Broken is not null) { e.Handled = true; return; }
            int bs = Theme.S(18);
            var box = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - bs) / 2, e.CellBounds.Y + (e.CellBounds.Height - bs) / 2, bs, bs);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (e.ColumnIndex == ColActive)
            {
                bool on = r.Code == active;
                using var pen = new Pen(on ? Theme.PrimaryBorder : Theme.ButtonBorder, 1.4f);
                using var fill = new SolidBrush(Theme.Input);
                e.Graphics.FillEllipse(fill, box);
                e.Graphics.DrawEllipse(pen, box);
                if (on)
                {
                    using var dot = new SolidBrush(Theme.Primary);
                    int d = Theme.S(5);
                    e.Graphics.FillEllipse(dot, Rectangle.Inflate(box, -d, -d));
                }
            }
            else
            {
                bool on = r.Installed;
                bool locked = Locked(r);
                using GraphicsPath path = Theme.RoundedRect(box, Theme.S(4));
                using var fill = new SolidBrush(on ? (locked ? Theme.ButtonBorder : Theme.Primary) : Theme.Input);
                using var pen = new Pen(on ? (locked ? Theme.ButtonBorder : Theme.PrimaryBorder) : Theme.ButtonBorder, 1.4f);
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(pen, path);
                if (on)
                {
                    using var tick = new Pen(Color.White, Theme.S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    float k = bs / 20f;
                    e.Graphics.DrawLines(tick, new[] { new PointF(box.X + 5 * k, box.Y + 10.5f * k), new PointF(box.X + 8.5f * k, box.Y + 14 * k), new PointF(box.X + 15 * k, box.Y + 6.5f * k) });
                }
            }
            e.Handled = true;
        };
        grid.MouseDown += (_, e) =>
        {
            DataGridView.HitTestInfo hit = grid.HitTest(e.X, e.Y);
            if (hit.Type != DataGridViewHitTestType.Cell || grid.Rows[hit.RowIndex].Tag is not Row r || r.Broken is not null) return;
            if (hit.ColumnIndex == ColInstalled)
            {
                if (Locked(r) && r.Installed) return;
                r.Installed = !r.Installed;
                if (!r.Installed && active == r.Code) active = Loc.Language;
            }
            else
            {
                active = r.Code;
                r.Installed = true;   // choosing a language installs it
            }
            grid.Invalidate();
        };

        var ok = Ui.Button("Übernehmen", "Apply", ButtonKind.Primary);
        var cancel = Ui.Button("Abbrechen", "Cancel");
        var folder = Ui.Button("Sprachordner öffnen", "Open language folder");
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        folder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(Languages.UserDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Languages.UserDirectory}\"") { UseShellExecute = true });
            }
            catch { }
        };
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Color.Transparent };
        cancel.Margin = new Padding(0);
        right.Controls.Add(cancel);
        right.Controls.Add(ok);
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Color.Transparent };
        left.Controls.Add(folder);
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(46), Padding = new Padding(0, Theme.S(10), 0, 0), BackColor = Color.Transparent };
        buttons.Controls.Add(left);
        buttons.Controls.Add(right);
        dialog.Controls.Add(host);
        dialog.Controls.Add(info);
        dialog.Controls.Add(status);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        // Online list in the background
        using var cts = new CancellationTokenSource();
        dialog.Shown += async (_, _) =>
        {
            try
            {
                (List<OnlineLanguage> online, List<BrokenLanguage> brokenOnline) = await Languages.OnlineWithErrorsAsync(cts.Token);
                if (dialog.IsDisposed) return;
                foreach (BrokenLanguage b in brokenOnline)
                    rows.Add(new Row { Code = b.Code, Name = b.FileName, Broken = b });
                foreach (OnlineLanguage o in online)
                {
                    Row? r = rows.FirstOrDefault(x => x.Code == o.Code && x.Broken is null);
                    if (r is null) rows.Add(new Row { Code = o.Code, Name = o.Name, Online = o });
                    else if (!r.BuiltIn) r.Online = o;
                }
                Fill();
                status.Text = online.Count == 0
                    ? Loc.T("Online sind (noch) keine weiteren Sprachen verfügbar.", "No further languages are available online (yet).")
                    : Loc.T($"{online.Count} Sprache(n) online verfügbar.", $"{online.Count} language(s) available online.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!dialog.IsDisposed)
                    status.Text = Loc.T($"Online-Liste nicht erreichbar: {ex.Message}", $"Online list not reachable: {ex.Message}");
            }
        };

        DialogResult result = Ui.ShowDialog(dialog, owner);
        cts.Cancel();
        if (result != DialogResult.OK) return null;

        var errors = new List<string>();
        foreach (Row r in rows.Where(r => !r.BuiltIn && r.Broken is null))
        {
            try
            {
                if (r.Installed && r.Online is not null && r.Newer) Languages.Install(r.Online);
                else if (!r.Installed && r.Local is not null && !Locked(r)) Languages.Remove(r.Local);
            }
            catch (Exception ex) { errors.Add($"{r.Name}: {ex.Message}"); }
        }
        if (errors.Count > 0)
            MessageBox.Show(owner, string.Join("\n", errors), Loc.T("Sprachen", "Languages"), MessageBoxButtons.OK, MessageBoxIcon.Warning);

        Row? chosen = rows.FirstOrDefault(r => r.Code == active && r.Broken is null);
        bool available = chosen is not null && (chosen.BuiltIn || Languages.Load(chosen.Code) is not null);
        return available ? active : Loc.Language;
    }
}
