using System.Drawing.Drawing2D;
using AdbTool.Backup;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>Tab 2: APK / OBB / Data backup (former APK Backup Tool).</summary>
internal sealed class BackupPage : UserControl, IPage
{
    private const int ColIcon = 0, ColApp = 1, ColApk = 2, ColObb = 3, ColData = 4, ColObbState = 5, ColDataState = 6, ColUninstall = 7;
    private static readonly Font TrashFont = new("Segoe MDL2 Assets", 11f);
    private int _hoverUninstall = -1;

    private readonly AppState _state;
    private readonly DataGridView _grid = new();
    private readonly TextBox _search = Ui.TextBox(320);
    private readonly DarkButton _scan = Ui.Button("Apps einlesen", "Scan apps", ButtonKind.Primary);
    private readonly DarkButton _clearSearch = Ui.Button("Suche löschen", "Clear search");
    private readonly DarkButton _selectAll = Ui.Button("Alle auswählen", "Select all");
    private readonly DarkButton _selectNone = Ui.Button("Alle abwählen", "Deselect all");
    private readonly Label _count = Ui.Value("");
    private readonly TextBox _destination = Ui.TextBox(520);
    private readonly DarkButton _browse = Ui.Button("Ordner wählen …", "Choose folder …");
    private readonly CheckBox _bundleSplits = Ui.CheckBox("Split-Apps als eine .apks-Datei speichern (wie SAI / AnExplorer)", "Save split apps as one .apks file (like SAI / AnExplorer)");
    private readonly DarkButton _backup = Ui.Button("Auswahl sichern", "Back up selection", ButtonKind.Primary);
    private readonly DarkButton _abort = Ui.Button("Abbrechen", "Cancel", ButtonKind.Danger);
    private readonly DarkButton _openFolder = Ui.Button("Backup öffnen", "Open backup");
    private readonly DarkProgressBar _progress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 26, Margin = new Padding(8, 4, 0, 4) };
    private readonly Label _status = Ui.Value("");
    private readonly TextBox _log = Ui.Console();

    private readonly Dictionary<string, Bitmap> _icons = new();
    private List<AppEntry> _apps = new();
    private string _scannedSerial = "";
    private string _lastBackup = "";
    private CancellationTokenSource? _cts;
    private Label? _hint;

    public BackupPage(AppState state)
    {
        _state = state;
        BackColor = Theme.Background;
        DoubleBuffered = true;
        Padding = new Padding(20, 14, 20, 14);

        BuildUi();

        // Narrow window: hide secondary information instead of cutting it off.
        Resize += (_, _) =>
        {
            bool wide = ClientSize.Width >= Theme.S(1150);
            if (_hint is not null) _hint.Visible = wide;
            bool folders = ClientSize.Width >= Theme.S(900);
            _grid.Columns[ColObbState].Visible = folders;
            _grid.Columns[ColDataState].Visible = folders;
        };

        _state.SelectedDeviceChanged += () => { if (!_state.IsBusy) InvalidateScan(); };
        _state.BusyChanged += UpdateEnabled;
        Loc.LanguageChanged += () => { UpdateCount(); _grid.Invalidate(); };

        Loc.Bind(_status, t => { if (_status.Text.Length == 0 || _status.Tag as string == "ready") { _status.Text = t; _status.Tag = "ready"; } }, "Bereit", "Ready");
        UpdateCount();
        UpdateEnabled();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        void AddRow(Control c, SizeType type = SizeType.AutoSize, float height = 0)
        {
            root.RowStyles.Add(new RowStyle(type, height));
            c.Dock = DockStyle.Fill;
            root.Controls.Add(c, 0, root.RowCount++);
        }

        // Toolbar ---------------------------------------------------------
        Ui.Placeholder(_search, "App-Name oder Paketname suchen …", "Search app or package name …");
        _search.Margin = new Padding(12, 6, 8, 0);
        _search.TextChanged += (_, _) => FilterRows();
        _clearSearch.Click += (_, _) => _search.Clear();
        _scan.Click += async (_, _) => await ScanAsync();
        var hint = _hint = Ui.Label("App-Name, Version und Icon werden automatisch ermittelt.", "App name, version and icon are resolved automatically.", muted: true);
        hint.Margin = new Padding(8, 10, 0, 0);
        var toolbar = new TableLayoutPanel { ColumnCount = 4, RowCount = 1, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 10) };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _search.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _search.MinimumSize = new Size(120, 0);
        toolbar.Controls.Add(_scan, 0, 0);
        toolbar.Controls.Add(_search, 1, 0);
        toolbar.Controls.Add(_clearSearch, 2, 0);
        toolbar.Controls.Add(hint, 3, 0);
        AddRow(toolbar);

        // Table -----------------------------------------------------------
        SetupGrid();
        var gridHost = new Panel { BackColor = Theme.CardBorder, Padding = new Padding(1), Margin = new Padding(0, 0, 0, 8) };
        _grid.Dock = DockStyle.Fill;
        gridHost.Controls.Add(_grid);
        AddRow(gridHost, SizeType.Percent, 100);

        // Selection -------------------------------------------------------
        _selectAll.Click += (_, _) => SelectAll(true);
        _selectNone.Click += (_, _) => SelectAll(false);
        _count.Margin = new Padding(8, 10, 0, 0);
        AddRow(Ui.Row(_selectAll, _selectNone, _count));

        var note = Ui.Label(
            "Daten = zugängliche Android/data-Ordner. Private App-Daten, Logins und interne Datenbanken sind nicht enthalten. " +
            "Gesperrt oder nicht prüfbar = nicht auswählbar. ‚Alle‘ gilt für die gesamte Liste, auch bei aktiver Suche.",
            "Data = accessible Android/data folders. Private app data, logins and internal databases are not included. " +
            "Blocked or unavailable = cannot be selected. 'All' applies to the entire list, including filtered apps.", muted: true);
        note.AutoSize = true;
        Ui.WrapTo(note, this);
        note.Margin = new Padding(0, 4, 0, 8);
        AddRow(note);

        // Destination -----------------------------------------------------
        _destination.Text = _state.Settings.BackupDestination;
        _destination.Leave += (_, _) => SaveDestination();
        _destination.TextChanged += (_, _) => _state.Settings.BackupDestination = _destination.Text.Trim();
        _browse.Click += (_, _) => ChooseDestination();
        var destLabel = Ui.Label("Speicherort", "Destination");
        var destRow = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) };
        destRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        destRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _destination.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        destRow.Controls.Add(destLabel, 0, 0);
        destRow.Controls.Add(_destination, 1, 0);
        destRow.Controls.Add(_browse, 2, 0);
        AddRow(destRow);
        _bundleSplits.Checked = _state.Settings.BackupSplitsAsApks;
        _bundleSplits.CheckedChanged += (_, _) => { _state.Settings.BackupSplitsAsApks = _bundleSplits.Checked; _state.Settings.Save(); };
        _bundleSplits.Margin = new Padding(0, 0, 0, 6);
        AddRow(_bundleSplits);

        // Actions ---------------------------------------------------------
        _backup.Click += async (_, _) => await BackupAsync();
        _abort.Click += (_, _) => CancelOperation();
        _openFolder.Click += (_, _) => { if (Directory.Exists(_lastBackup)) SettingsPage.OpenPath(_lastBackup); };
        _openFolder.Enabled = false;
        var actions = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 4) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.Controls.Add(_backup, 0, 0);
        actions.Controls.Add(_abort, 1, 0);
        actions.Controls.Add(_openFolder, 2, 0);
        actions.Controls.Add(_progress, 3, 0);
        AddRow(actions);

        _status.AutoEllipsis = true;
        _status.Margin = new Padding(0, 6, 0, 6);
        AddRow(_status);

        Ui.Placeholder(_log, "Hier erscheinen Fortschritt und Fehlermeldungen.", "Progress and error messages appear here.");
        AddRow(_log, SizeType.Absolute, 90);
    }

    private void SetupGrid()
    {
        _grid.BackgroundColor = Theme.Card;
        _grid.BorderStyle = BorderStyle.None;
        _grid.GridColor = Theme.Grid;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.TableHeader, ForeColor = Theme.Text, Font = Theme.BoldFont,
            SelectionBackColor = Theme.TableHeader, SelectionForeColor = Theme.Text, Padding = new Padding(6, 0, 6, 0)
        };
        _grid.ColumnHeadersHeight = Theme.S(38);
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.BaseFont,
            SelectionBackColor = Theme.Selection, SelectionForeColor = Theme.Text, Padding = new Padding(4, 0, 4, 0)
        };
        _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.TableAlt };
        _grid.RowHeadersVisible = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.RowTemplate.Height = Theme.S(54);
        _grid.ReadOnly = true;
        Theme.UseDarkScrollBars(_grid);
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(_grid, true);

        _grid.Columns.Add(new DataGridViewImageColumn { Width = Theme.S(56), ImageLayout = DataGridViewImageCellLayout.Normal, Resizable = DataGridViewTriState.False, DefaultCellStyle = { NullValue = null, Alignment = DataGridViewContentAlignment.MiddleCenter } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = Theme.S(200) });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Width = Theme.S(64), HeaderText = "APK", Resizable = DataGridViewTriState.False });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Width = Theme.S(64), HeaderText = "OBB", Resizable = DataGridViewTriState.False });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Width = Theme.S(64), Resizable = DataGridViewTriState.False });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(150) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(150) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Width = Theme.S(52), Resizable = DataGridViewTriState.False });
        foreach (DataGridViewColumn c in _grid.Columns)
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
        _grid.Columns[ColApk].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.Columns[ColObb].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.Columns[ColData].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;

        Loc.Bind(_grid, t => _grid.Columns[ColApp].HeaderText = t, "App / Paket", "App / package");
        Loc.Bind(_grid, t => _grid.Columns[ColData].HeaderText = t, "Daten", "Data");
        Loc.Bind(_grid, t => _grid.Columns[ColObbState].HeaderText = t, "OBB-Ordner", "OBB folder");
        Loc.Bind(_grid, t => _grid.Columns[ColDataState].HeaderText = t, "Datenordner", "Data folder");

        _grid.CellMouseEnter += (_, e) =>
        {
            int row = e.ColumnIndex == ColUninstall ? e.RowIndex : -1;
            if (row != _hoverUninstall) { _hoverUninstall = row; _grid.InvalidateColumn(ColUninstall); }
            _grid.Cursor = row >= 0 ? Cursors.Hand : Cursors.Default;
        };
        _grid.MouseLeave += (_, _) =>
        {
            if (_hoverUninstall < 0) return;
            _hoverUninstall = -1;
            _grid.Cursor = Cursors.Default;
            _grid.InvalidateColumn(ColUninstall);
        };
        _grid.CellPainting += OnCellPainting;
        _grid.CellFormatting += OnCellFormatting;
        _grid.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            DataGridView.HitTestInfo hit = _grid.HitTest(e.X, e.Y);
            if (hit.Type != DataGridViewHitTestType.Cell) return;
            if (hit.ColumnIndex == ColUninstall)
                BeginInvoke(new Action(() => _ = UninstallAsync(hit.RowIndex)));
            else
                ToggleCell(hit.RowIndex, hit.ColumnIndex);
        };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space && _grid.CurrentCell is { } cell)
            {
                ToggleCell(cell.RowIndex, cell.ColumnIndex);
                e.Handled = true;
            }
        };
    }

    // ------------------------------------------------------------------
    // Table rendering
    // ------------------------------------------------------------------

    private static bool Available(AppEntry app, int column) => column switch
    {
        ColApk => app.Apks.Count > 0,
        ColObb => app.Obb == FolderState.Present,
        ColData => app.Data == FolderState.Present,
        _ => false
    };

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not AppEntry app)
            return;
        if (e.ColumnIndex == ColObbState || e.ColumnIndex == ColDataState)
        {
            FolderState state = e.ColumnIndex == ColObbState ? app.Obb : app.Data;
            e.Value = state.Text();
            e.CellStyle!.ForeColor = state == FolderState.Present ? Theme.Accent : Theme.Muted;
            e.CellStyle.SelectionForeColor = e.CellStyle.ForeColor;
            e.FormattingApplied = true;
        }
    }

    private void OnCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics is null || _grid.Rows[e.RowIndex].Tag is not AppEntry app)
            return;

        Graphics g = e.Graphics;
        bool selected = (e.State & DataGridViewElementStates.Selected) != 0;

        if (e.ColumnIndex == ColUninstall)
        {
            e.PaintBackground(e.CellBounds, selected);
            bool hover = e.RowIndex == _hoverUninstall && !_state.IsBusy;
            int size = Theme.S(30);
            var r = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - size) / 2, e.CellBounds.Y + (e.CellBounds.Height - size) / 2, size, size);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hover)
            {
                using GraphicsPath path = Theme.RoundedRect(r, Theme.S(6));
                using var fill = new SolidBrush(Theme.Danger);
                g.FillPath(fill, path);
            }
            TextRenderer.DrawText(g, "\uE74D", TrashFont, r, _state.IsBusy ? Theme.DisabledText : hover ? Color.White : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            e.Handled = true;
            return;
        }

        if (e.ColumnIndex == ColApp)
        {
            e.PaintBackground(e.CellBounds, selected);
            string title = app.Name + (app.Version.Length > 0 ? " · " + app.Version : "");
            int h = e.CellBounds.Height;
            var titleRect = new Rectangle(e.CellBounds.X + Theme.S(6), e.CellBounds.Y + h / 2 - Theme.S(22), e.CellBounds.Width - Theme.S(12), Theme.S(22));
            var pkgRect = new Rectangle(e.CellBounds.X + Theme.S(6), e.CellBounds.Y + h / 2, e.CellBounds.Width - Theme.S(12), Theme.S(20));
            TextRenderer.DrawText(g, title, Theme.BoldFont, titleRect, Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            string sub = app.Package + (app.Note.Length > 0 ? "  ⚠ " + app.Note : "");
            TextRenderer.DrawText(g, sub, Theme.BaseFont, pkgRect, app.Note.Length > 0 ? Theme.Warning : Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            e.Handled = true;
        }
        else if (e.ColumnIndex is ColApk or ColObb or ColData)
        {
            e.PaintBackground(e.CellBounds, selected);
            int bs = Theme.S(20);
            var box = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - bs) / 2, e.CellBounds.Y + (e.CellBounds.Height - bs) / 2, bs, bs);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (!Available(app, e.ColumnIndex))
            {
                TextRenderer.DrawText(g, "–", Theme.BaseFont, e.CellBounds, Theme.DisabledText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                bool isChecked = e.Value is true;
                using GraphicsPath path = Theme.RoundedRect(box, Theme.S(4));
                using var fill = new SolidBrush(isChecked ? Theme.Primary : Theme.Input);
                using var pen = new Pen(isChecked ? Theme.PrimaryBorder : Theme.ButtonBorder, 1.4f);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
                if (isChecked)
                {
                    using var tick = new Pen(Color.White, Theme.S(2.2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    float k = bs / 20f;
                    g.DrawLines(tick, new[] { new PointF(box.X + 5 * k, box.Y + 10.5f * k), new PointF(box.X + 8.5f * k, box.Y + 14 * k), new PointF(box.X + 15 * k, box.Y + 6.5f * k) });
                }
            }
            e.Handled = true;
        }
    }

    private void ToggleCell(int row, int column)
    {
        if (row < 0 || column is not (ColApk or ColObb or ColData) || _state.IsBusy)
            return;
        if (_grid.Rows[row].Tag is not AppEntry app || !Available(app, column))
            return;
        DataGridViewCell cell = _grid.Rows[row].Cells[column];
        bool next = cell.Value is not true;
        cell.Value = next;
        _grid.InvalidateCell(cell);
        UpdateCount();
    }

    private Bitmap IconFor(AppEntry app)
    {
        if (_icons.TryGetValue(app.Package, out Bitmap? cached))
            return cached;

        Bitmap result;
        using (Bitmap? decoded = ImageLoader.Decode(app.Icon))
        {
            result = decoded is not null ? ImageLoader.Thumbnail(decoded, Theme.S(40)) : LetterIcon(app.Name);
        }
        _icons[app.Package] = result;
        return result;
    }

    private static Bitmap LetterIcon(string name)
    {
        int size = Theme.S(40);
        var bmp = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using GraphicsPath path = Theme.RoundedRect(new Rectangle(0, 0, size - 1, size - 1), Theme.S(9));
        using var brush = new SolidBrush(Theme.Button);
        using var pen = new Pen(Theme.ButtonBorder);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
        string letter = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
        TextRenderer.DrawText(g, letter, Theme.SectionFont, new Rectangle(0, 0, size, size), Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        return bmp;
    }

    private void Populate()
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (AppEntry app in _apps)
        {
            int index = _grid.Rows.Add(IconFor(app), app.Name, false, false, false, "", "", "");
            DataGridViewRow row = _grid.Rows[index];
            row.Tag = app;
            row.Cells[ColUninstall].ToolTipText = Loc.T("App vom Gerät deinstallieren", "Uninstall app from the device");
            row.Cells[ColApp].ToolTipText = app.Note.Length > 0
                ? app.Note
                : Loc.T($"{app.Apks.Count} APK-Datei(en), inklusive Split-APKs", $"{app.Apks.Count} APK file(s), including split APKs");
        }
        _grid.ResumeLayout();
        FilterRows();
        UpdateCount();
    }

    private void FilterRows()
    {
        string query = _search.Text.Trim();
        _grid.CurrentCell = null;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is AppEntry app)
                row.Visible = query.Length == 0 ||
                    $"{app.Name} {app.Version} {app.Package}".Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private void SelectAll(bool selected)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not AppEntry app) continue;
            foreach (int col in new[] { ColApk, ColObb, ColData })
                if (Available(app, col))
                    row.Cells[col].Value = selected;
        }
        _grid.Invalidate();
        UpdateCount();
    }

    private List<BackupSelection> Selections()
    {
        var result = new List<BackupSelection>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not AppEntry app) continue;
            var sel = new BackupSelection(app, row.Cells[ColApk].Value is true, row.Cells[ColObb].Value is true, row.Cells[ColData].Value is true);
            if (sel.Apk || sel.Obb || sel.Data)
                result.Add(sel);
        }
        return result;
    }

    private void UpdateCount()
    {
        if (_apps.Count == 0)
        {
            _count.Text = Loc.T("Noch keine Apps eingelesen", "No apps scanned yet");
        }
        else
        {
            List<BackupSelection> sel = Selections();
            int components = sel.Sum(s => s.Kinds().Count());
            _count.Text = Loc.T($"{_apps.Count} Apps · {sel.Count} ausgewählt · {components} Komponenten",
                $"{_apps.Count} apps · {sel.Count} selected · {components} components");
        }
        UpdateEnabled();
    }

    private void InvalidateScan()
    {
        _apps = new List<AppEntry>();
        _scannedSerial = "";
        _grid.Rows.Clear();
        UpdateCount();
    }

    // ------------------------------------------------------------------
    // Operations
    // ------------------------------------------------------------------

    public void OnActivated() => UpdateEnabled();

    public void CancelOperation()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _abort.Enabled = false;
        SetStatus(Loc.T("Vorgang wird abgebrochen …", "Cancelling …"));
    }

    private void UpdateEnabled()
    {
        bool mine = _cts is not null;
        bool idle = !_state.IsBusy;
        _scan.Enabled = idle;
        _selectAll.Enabled = _selectNone.Enabled = idle && _apps.Count > 0;
        _destination.Enabled = _browse.Enabled = _bundleSplits.Enabled = idle;
        _backup.Enabled = idle && _apps.Count > 0;
        _abort.Enabled = mine && !_cts!.IsCancellationRequested;
    }

    private void SetStatus(string message)
    {
        _status.Tag = null;
        _status.Text = message.Replace("\n", "  ");
    }

    private void OnMessage(string message)
    {
        SetStatus(message);
        Ui.AppendLine(_log, message);
    }

    private void OnProgress((int Current, int Total) p)
    {
        _progress.Marquee = false;
        _progress.Maximum = Math.Max(1, p.Total);
        _progress.Value = p.Current;
    }

    private AdbDevice? ReadyDevice()
    {
        AdbDevice? device = _state.SelectedDevice;
        if (!_state.HasAdb)
        {
            MessageBox.Show(FindForm(), Loc.T("ADB ist nicht eingerichtet. Bitte im Tab „Allgemeine Einstellungen“ adb.exe auswählen.",
                "ADB is not set up. Please choose adb.exe in the \"General settings\" tab."), Loc.T("ADB fehlt", "ADB missing"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        if (device is null || !device.IsReady)
        {
            MessageBox.Show(FindForm(), Loc.T(
                    "Bitte ein verbundenes, autorisiertes Gerät auswählen.\nBei ‚unauthorized‘ den RSA-Dialog auf dem Android-Gerät bestätigen und erneut suchen.",
                    "Select a connected, authorized device.\nIf it says 'unauthorized', accept the RSA prompt on Android and find devices again."),
                Loc.T("Gerät nicht bereit", "Device not ready"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        return device;
    }

    private async Task ScanAsync()
    {
        AdbDevice? device = ReadyDevice();
        if (device is null || !_state.TryBeginOperation(Loc.T("Apps werden eingelesen", "Scanning apps")))
            return;

        InvalidateScan();
        _cts = new CancellationTokenSource();
        _progress.Marquee = true;
        UpdateEnabled();

        var log = new Progress<string>(OnMessage);
        var progress = new Progress<(int, int)>(OnProgress);
        var service = new BackupService(new AdbClient(_state.AdbPath!, device.Serial),
            ((IProgress<string>)log).Report, (c, t) => ((IProgress<(int, int)>)progress).Report((c, t)), _cts.Token);

        try
        {
            List<AppEntry> apps = await Task.Run(() => service.ScanAsync(resolveNames: true));
            _apps = apps;
            _scannedSerial = device.Serial;
            Populate();
            OnMessage(Loc.T($"{apps.Count} nachinstallierte Apps eingelesen. APK, OBB und Daten für die Sicherung auswählen.",
                $"Scanned {apps.Count} user-installed apps. Select APK, OBB and Data for backup."));
        }
        catch (OperationCanceledException)
        {
            OnMessage(Loc.T("Vorgang abgebrochen.", "Operation cancelled."));
        }
        catch (Exception ex)
        {
            OnMessage(ex.Message);
            MessageBox.Show(FindForm(), ex.Message, Loc.T("Vorgang nicht abgeschlossen", "Operation incomplete"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Finish();
        }
    }

    private async Task BackupAsync()
    {
        List<BackupSelection> selections = Selections();
        if (selections.Count == 0)
        {
            MessageBox.Show(FindForm(), Loc.T("Bitte mindestens ein Feld APK, OBB oder Daten auswählen.", "Select at least one APK, OBB or Data checkbox."),
                Loc.T("Keine Auswahl", "Nothing selected"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AdbDevice? device = ReadyDevice();
        if (device is null)
            return;
        if (device.Serial != _scannedSerial)
        {
            MessageBox.Show(FindForm(), Loc.T("Die App-Liste gehört zu einem anderen Gerät. Bitte Apps erneut einlesen.",
                "The app list belongs to a different device. Please scan the apps again."), "ADBora");
            return;
        }

        string destinationText = _destination.Text.Trim();
        if (destinationText.Length == 0)
        {
            MessageBox.Show(FindForm(), Loc.T("Bitte einen Backup-Ordner wählen.", "Please choose a backup folder."),
                Loc.T("Speicherort fehlt", "Destination missing"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string destination;
        try { destination = Path.GetFullPath(Environment.ExpandEnvironmentVariables(destinationText)); }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(), ex.Message, Loc.T("Speicherort ungültig", "Invalid destination"));
            return;
        }
        SaveDestination();

        if (!_state.TryBeginOperation(Loc.T("Backup läuft", "Backup running")))
            return;

        _cts = new CancellationTokenSource();
        _progress.Marquee = true;
        UpdateEnabled();

        var log = new Progress<string>(OnMessage);
        var progress = new Progress<(int, int)>(OnProgress);
        var service = new BackupService(new AdbClient(_state.AdbPath!, device.Serial),
            ((IProgress<string>)log).Report, (c, t) => ((IProgress<(int, int)>)progress).Report((c, t)), _cts.Token)
        {
            BundleSplits = _bundleSplits.Checked
        };

        try
        {
            BackupReport report = await Task.Run(() => service.BackupAsync(selections, destination, device.Model));
            _lastBackup = report.Directory;
            _openFolder.Enabled = true;

            int errors = report.Apps.Sum(a => a.Errors.Count);
            int completed = report.Apps.Sum(a => a.Completed.Count);
            int skipped = report.Apps.Sum(a => a.Skipped.Count);
            string message = (report.Cancelled ? Loc.T("Backup abgebrochen", "Backup cancelled") : Loc.T("Backup beendet", "Backup finished")) +
                             Loc.T($": {completed} Komponenten gesichert, {errors} Fehler.", $": {completed} components saved, {errors} errors.") +
                             (skipped > 0 ? Loc.T($"\n{skipped} Datei(en) ohne Leserechte übersprungen (meist Caches, die die App selbst neu erzeugt).",
                                                  $"\n{skipped} file(s) without read permission skipped (usually caches the app recreates itself).") : "") +
                             "\n" + Loc.T($"Gerät: {report.DeviceModel} ({report.Device})", $"Device: {report.DeviceModel} ({report.Device})");
            OnMessage(message + Loc.T($"\nBericht: {Path.Combine(report.Directory, "backup-report.json")}", $"\nReport: {Path.Combine(report.Directory, "backup-report.json")}"));

            if (errors > 0 || report.Cancelled)
            {
                string details = string.Join("\n", report.Apps.SelectMany(a => a.Errors.Select(e => $"{a.Name}: {e}")));
                MessageBox.Show(FindForm(),
                    message + Loc.T("\nDetails stehen im Backup-Bericht. .partial-Ordner sind unvollständig.", "\nSee the backup report for details. .partial folders are incomplete.") +
                    (details.Length > 0 ? "\n\n" + (details.Length > 2500 ? details[..2500] + " …" : details) : ""),
                    Loc.T("Backup unvollständig", "Backup incomplete"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (skipped > 0)
            {
                string details = string.Join("\n", report.Apps.Where(a => a.Skipped.Count > 0)
                    .Select(a => $"{a.Name}: " + string.Join(", ", a.Skipped.Take(3).Select(Path.GetFileName)) + (a.Skipped.Count > 3 ? $" (+{a.Skipped.Count - 3})" : "")));
                MessageBox.Show(FindForm(), message + "\n\n" + (details.Length > 2000 ? details[..2000] + " …" : details) +
                    Loc.T("\n\nDie vollständige Liste steht in manifest.json der App („skipped_unreadable“).", "\n\nThe full list is in the app's manifest.json (\"skipped_unreadable\")."),
                    Loc.T("Backup abgeschlossen (mit Hinweisen)", "Backup complete (with notes)"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(FindForm(), message, Loc.T("Backup abgeschlossen", "Backup complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (OperationCanceledException)
        {
            OnMessage(Loc.T("Vorgang abgebrochen.", "Operation cancelled."));
        }
        catch (Exception ex)
        {
            OnMessage(ex.Message);
            MessageBox.Show(FindForm(), ex.Message, Loc.T("Vorgang nicht abgeschlossen", "Operation incomplete"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>Uninstalls one app from the scanned device (after confirmation).</summary>
    private async Task UninstallAsync(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _grid.Rows.Count || _grid.Rows[rowIndex].Tag is not AppEntry app || _state.IsBusy)
            return;
        AdbDevice? device = ReadyDevice();
        if (device is null)
            return;
        if (device.Serial != _scannedSerial)
        {
            MessageBox.Show(FindForm(), Loc.T("Die App-Liste gehört zu einem anderen Gerät. Bitte Apps erneut einlesen.",
                "The app list belongs to a different device. Please scan the apps again."), "ADBora");
            return;
        }

        string title = app.Name + (app.Version.Length > 0 ? " " + app.Version : "");
        if (MessageBox.Show(FindForm(),
                Loc.T($"„{title}“ ({app.Package}) von {device.DisplayName} deinstallieren?\n\nDie App und ihre Daten auf dem Gerät werden entfernt. Das kann nicht rückgängig gemacht werden – vorher ggf. ein Backup erstellen.",
                      $"Uninstall \"{title}\" ({app.Package}) from {device.DisplayName}?\n\nThe app and its data on the device will be removed. This cannot be undone – create a backup first if needed."),
                Loc.T("App deinstallieren", "Uninstall app"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        if (!_state.TryBeginOperation(Loc.T("Deinstallation läuft", "Uninstalling")))
            return;
        _progress.Marquee = true;
        try
        {
            OnMessage(Loc.T($"Deinstalliere {title} ({app.Package}) …", $"Uninstalling {title} ({app.Package}) …"));
            AdbResult result = await new AdbClient(_state.AdbPath!, device.Serial)
                .CaptureAsync(CancellationToken.None, TimeSpan.FromMinutes(2), "uninstall", app.Package);
            if (result.Ok && result.Combined.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                OnMessage(Loc.T($"✔ {title} wurde deinstalliert.", $"✔ {title} was uninstalled."));
                _apps.Remove(app);
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.Tag == app) { _grid.Rows.Remove(row); break; }
                }
                UpdateCount();
            }
            else
            {
                string detail = result.Combined.Trim();
                OnMessage(Loc.T($"FEHLER · Deinstallation von {title}: {detail}", $"ERROR · Uninstalling {title}: {detail}"));
                MessageBox.Show(FindForm(), Loc.T($"Deinstallation fehlgeschlagen:\n{detail}", $"Uninstall failed:\n{detail}"),
                    "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            OnMessage(ex.Message);
        }
        finally
        {
            _progress.Marquee = false;
            _state.EndOperation();
            UpdateEnabled();
            _grid.InvalidateColumn(ColUninstall);
        }
    }

    private void Finish()
    {
        _cts?.Dispose();
        _cts = null;
        _progress.Marquee = false;
        if (_progress.Value == 0) _progress.Maximum = 100;
        _state.EndOperation();
        UpdateEnabled();
    }

    private void SaveDestination()
    {
        _state.Settings.BackupDestination = _destination.Text.Trim();
        _state.Settings.Save();
    }

    private void ChooseDestination()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Loc.T("Backup-Speicherort", "Backup destination"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (Directory.Exists(_destination.Text))
            dialog.SelectedPath = _destination.Text;
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _destination.Text = dialog.SelectedPath;
            SaveDestination();
        }
    }
}
