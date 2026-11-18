using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using AdbTool.Backup;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>Tab 2: APK / OBB / Data backup (former APK Backup Tool).</summary>
internal sealed class BackupPage : DockPanel, IPage
{
    /// <summary>One app in the list with its three check boxes.</summary>
    private sealed class AppRow : INotifyPropertyChanged
    {
        public AppRow(AppEntry app) => App = app;
        public AppEntry App { get; }
        private bool _apk, _obb, _data;
        public bool Apk { get => _apk; set { _apk = value; Changed(nameof(Apk)); } }
        public bool Obb { get => _obb; set { _obb = value; Changed(nameof(Obb)); } }
        public bool Data { get => _data; set { _data = value; Changed(nameof(Data)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private const string Columns = "56,*,64,64,64,150,150,52";

    private readonly AppState _state;
    private readonly ListBox _list = new();
    private readonly Grid _header = new() { ColumnDefinitions = new ColumnDefinitions(Columns) };
    private readonly TextBox _search = new() { Margin = new Thickness(12, 0, 8, 0), MinWidth = 120 };
    private readonly Button _scan = Ui.Button("Apps einlesen", "Scan apps", ButtonKind.Primary);
    private readonly Button _clearSearch = Ui.Button("Suche löschen", "Clear search");
    // Sort order: 0 = last installed first (default at every start), 1 = alphabetical
    private readonly ComboBox _sort = Ui.Combo(200);
    private bool _sorting;
    private readonly Button _selectAll = Ui.Button("Alle auswählen", "Select all");
    private readonly Button _selectNone = Ui.Button("Alle abwählen", "Deselect all");
    private readonly TextBlock _count = Ui.Value("");
    private readonly TextBox _destination = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _browse = Ui.Button("Ordner wählen …", "Choose folder …");
    private readonly CheckBox _bundleSplits = Ui.CheckBox("Split-Apps als eine .apks-Datei speichern (wie SAI / AnExplorer)", "Save split apps as one .apks file (like SAI / AnExplorer)");
    private readonly Button _backup = Ui.Button("Auswahl sichern", "Back up selection", ButtonKind.Primary);
    private readonly Button _abort = Ui.Button("Abbrechen", "Cancel", ButtonKind.Danger);
    private readonly Button _openFolder = Ui.Button("Backup öffnen", "Open backup");
    private readonly ProgressBar _progress = new() { Height = 8, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = Ui.Value("");
    private readonly TextBox _log = Ui.Console();
    private readonly List<Control> _folderColumns = new();
    private TextBlock? _hint;

    private readonly Dictionary<string, Bitmap> _icons = new();
    private List<AppEntry> _apps = new();
    private List<AppRow> _rows = new();
    private string _scannedSerial = "";
    private string _lastBackup = "";
    private CancellationTokenSource? _cts;
    private bool _statusIsReady = true;

    public BackupPage(AppState state)
    {
        _state = state;
        Margin = new Thickness(20, 14);

        BuildUi();

        // Narrow window: hide secondary information instead of cutting it off.
        SizeChanged += (_, e) =>
        {
            if (_hint is not null) _hint.IsVisible = e.NewSize.Width >= 1150;
            ShowFolderColumns(e.NewSize.Width >= 900);
        };

        _state.SelectedDeviceChanged += () => { if (!_state.IsBusy) InvalidateScan(); };
        _state.BusyChanged += UpdateEnabled;
        Loc.LanguageChanged += () => { UpdateCount(); Populate(); };

        Loc.Bind(_status, t => { if (_statusIsReady) _status.Text = t; }, "Bereit", "Ready");
        UpdateCount();
        UpdateEnabled();
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private bool _foldersShown = true;
    private void ShowFolderColumns(bool show)
    {
        if (show == _foldersShown) return;
        _foldersShown = show;
        foreach (Grid g in new[] { _header }.Concat(_list.GetRealizedContainers().Select(c => (c as ListBoxItem)?.Content as Grid).OfType<Grid>()))
            ApplyColumns(g);
    }

    private void ApplyColumns(Grid g)
    {
        g.ColumnDefinitions[5].Width = g.ColumnDefinitions[6].Width = _foldersShown ? new GridLength(150) : new GridLength(0);
    }

    private void BuildUi()
    {
        // Toolbar ---------------------------------------------------------
        Ui.Placeholder(_search, "App-Name oder Paketname suchen …", "Search app or package name …");
        _search.TextChanged += (_, _) => FilterRows();
        _clearSearch.Click += (_, _) => _search.Text = "";
        _scan.Click += async (_, _) => await ScanAsync();
        void SortItems()
        {
            int selected = Math.Max(0, _sort.SelectedIndex);
            _sorting = true;
            _sort.ItemsSource = new[] { Loc.T("Zuletzt installiert", "Last installed"), Loc.T("Alphabetisch", "Alphabetical") };
            _sort.SelectedIndex = selected;
            _sorting = false;
        }
        SortItems();
        Loc.LanguageChanged += SortItems;
        _sort.Margin = default;
        _sort.SelectionChanged += (_, _) => { if (!_sorting) Resort(); };
        var hint = _hint = Ui.Label("App-Name, Version und Icon werden automatisch ermittelt.", "App name, version and icon are resolved automatically.", muted: true);
        hint.Margin = new Thickness(12, 0, 0, 0);
        hint.TextWrapping = TextWrapping.NoWrap;
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        Control[] tools = { _scan, _search, _clearSearch, _sort, hint };
        for (int i = 0; i < tools.Length; i++) { Grid.SetColumn(tools[i], i); tools[i].VerticalAlignment = VerticalAlignment.Center; toolbar.Children.Add(tools[i]); }
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        // Bottom part (docked from the bottom up) ----------------------------
        _log.Height = 90;
        _log.Margin = new Thickness(0, 4, 0, 0);
        Ui.Placeholder(_log, "Hier erscheinen Fortschritt und Fehlermeldungen.", "Progress and error messages appear here.");
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.TextWrapping = TextWrapping.NoWrap;

        _backup.Click += async (_, _) => await BackupAsync();
        _abort.Click += (_, _) => CancelOperation();
        _openFolder.Click += (_, _) => { if (Directory.Exists(_lastBackup)) Platform.OpenFolder(_lastBackup); };
        _openFolder.IsEnabled = false;
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"), Margin = new Thickness(0, 4, 0, 4) };
        Control[] act = { _backup, _abort, _openFolder, _progress };
        for (int i = 0; i < act.Length; i++) { Grid.SetColumn(act[i], i); actions.Children.Add(act[i]); }

        _destination.Text = Privacy.ShowPath(_state.Settings.BackupDestination);
        Privacy.Changed += () => _destination.Text = Privacy.ShowPath((_destination.Text ?? "").Trim());
        _destination.LostFocus += (_, _) => SaveDestination();
        _destination.TextChanged += (_, _) => _state.Settings.BackupDestination = (_destination.Text ?? "").Trim();
        _browse.Click += async (_, _) => await ChooseDestination();
        var destLabel = Ui.Label("Speicherort", "Destination");
        var destRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        Grid.SetColumn(_destination, 1); Grid.SetColumn(_browse, 2);
        _browse.Margin = default;
        destRow.Children.Add(destLabel); destRow.Children.Add(_destination); destRow.Children.Add(_browse);
        _bundleSplits.IsChecked = _state.Settings.BackupSplitsAsApks;
        _bundleSplits.IsCheckedChanged += (_, _) => { _state.Settings.BackupSplitsAsApks = _bundleSplits.IsChecked == true; _state.Settings.Save(); };
        _bundleSplits.Margin = new Thickness(0, 0, 0, 6);

        _selectAll.Click += (_, _) => SelectAll(true);
        _selectNone.Click += (_, _) => SelectAll(false);
        _count.Margin = new Thickness(8, 0, 0, 0);
        var note = Ui.Label(
            "Daten = Android/data-Ordner; bei debuggbaren Apps („+ intern“) zusätzlich die internen App-Daten (Spielstände, Einstellungen) per run-as. Bei anderen Apps sind interne Daten für ADB gesperrt. " +
            "Gesperrt oder nicht prüfbar = nicht auswählbar. ‚Alle‘ gilt für die gesamte Liste, auch bei aktiver Suche.",
            "Data = Android/data folders; for debuggable apps (\"+ internal\") also the internal app data (save games, settings) via run-as. For other apps, internal data is locked for ADB. " +
            "Blocked or unavailable = cannot be selected. 'All' applies to the entire list, including filtered apps.", muted: true);
        note.Margin = new Thickness(0, 4, 0, 8);

        var bottom = new StackPanel { Children = { Ui.Row(_selectAll, _selectNone, _count), note, destRow, _bundleSplits, actions, _status, _log } };
        SetDock(bottom, Dock.Bottom);
        Children.Add(bottom);

        // Table -----------------------------------------------------------
        SetupList();
        var table = new DockPanel();
        SetDock(_header, Dock.Top);
        table.Children.Add(_header);
        table.Children.Add(_list);
        Children.Add(new Border
        {
            BorderBrush = AppTheme.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Background = AppTheme.Card, Margin = new Thickness(0, 0, 0, 8), ClipToBounds = true, Child = table, MinHeight = 220,
        });
    }

    private void SetupList()
    {
        _header.Background = AppTheme.TableHeader;
        _header.MinHeight = 38;
        void H(int col, string de, string en, bool center = false)
        {
            var t = Ui.Bind(new TextBlock { FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 0), HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left }, de, en);
            Grid.SetColumn(t, col);
            _header.Children.Add(t);
        }
        H(1, "App / Paket", "App / package");
        H(2, "APK", "APK", true);
        H(3, "OBB", "OBB", true);
        H(4, "Daten", "Data", true);
        H(5, "OBB-Ordner", "OBB folder");
        H(6, "Datenordner", "Data folder");

        _list.Background = Brushes.Transparent;
        _list.ItemTemplate = new FuncDataTemplate<AppRow>((row, _) => row is null ? new Border() : BuildRow(row));
        _list.Classes.Add("apps");
    }

    private Control BuildRow(AppRow row)
    {
        AppEntry app = row.App;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), MinHeight = 54, Background = Brushes.Transparent };
        ApplyColumns(grid);

        var icon = new Image { Source = IconFor(app), Width = 40, Height = 40 };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        string title = app.Name + (app.Version.Length > 0 ? " · " + app.Version : "");
        string sub = app.Package + (app.Note.Length > 0 ? "  ⚠ " + app.Note : "");
        var names = new StackPanel
        {
            Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = sub, Foreground = app.Note.Length > 0 ? AppTheme.Warning : AppTheme.Muted, TextTrimming = TextTrimming.CharacterEllipsis },
            }
        };
        string tip = app.Note.Length > 0 ? app.Note : Loc.T($"{app.Apks.Count} APK-Datei(en), inklusive Split-APKs", $"{app.Apks.Count} APK file(s), including split APKs");
        if (app.InstallTime > 0)
            tip += "\n" + Loc.T("Installiert/aktualisiert: ", "Installed/updated: ") + DateTimeOffset.FromUnixTimeSeconds(app.InstallTime).LocalDateTime.ToString("g");
        ToolTip.SetTip(names, tip);
        Grid.SetColumn(names, 1);
        grid.Children.Add(names);

        Control Check(int col, Func<bool> get, Action<bool> set, bool available)
        {
            Control c;
            if (!available)
                c = new TextBlock { Text = "–", Foreground = AppTheme.DisabledText, HorizontalAlignment = HorizontalAlignment.Center };
            else
            {
                var box = new CheckBox { IsChecked = get(), HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 0, Padding = default };
                box.IsCheckedChanged += (_, _) => { set(box.IsChecked == true); UpdateCount(); };
                row.PropertyChanged += (_, _) => { if (box.IsChecked != get()) box.IsChecked = get(); };
                box.IsEnabled = !_state.IsBusy;
                c = box;
            }
            Grid.SetColumn(c, col);
            grid.Children.Add(c);
            return c;
        }
        Check(2, () => row.Apk, v => row.Apk = v, Available(app, 2));
        Check(3, () => row.Obb, v => row.Obb = v, Available(app, 3));
        Check(4, () => row.Data, v => row.Data = v, Available(app, 4));

        TextBlock State(int col, FolderState state, bool data)
        {
            string text = state.Text();
            bool ok = state == FolderState.Present;
            if (data && app.Debuggable)
            {
                // Debuggable apps: internal data (save games etc.) is backed up too, via run-as
                text = ok ? Loc.T("Vorhanden + intern", "Available + internal") : Loc.T("Nur intern", "Internal only");
                ok = true;
            }
            var t = new TextBlock { Text = text, Foreground = ok ? AppTheme.Accent : AppTheme.Muted, Margin = new Thickness(6, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
            return t;
        }
        State(5, app.Obb, false);
        State(6, app.Data, true);

        var trash = new Button
        {
            Content = new TrashIcon { Width = 16, Height = 16 },
            Width = 32, Height = 32, MinHeight = 0, Padding = default,
            Background = Brushes.Transparent, BorderThickness = default,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsEnabled = !_state.IsBusy,
        };
        trash.Classes.Add("trash");
        Loc.Bind(trash, t => ToolTip.SetTip(trash, t), "App vom Gerät deinstallieren", "Uninstall app from the device");
        trash.Click += async (_, _) => await UninstallAsync(row);
        Grid.SetColumn(trash, 7);
        grid.Children.Add(trash);
        return grid;
    }

    // ------------------------------------------------------------------
    // Table
    // ------------------------------------------------------------------

    private static bool Available(AppEntry app, int column) => column switch
    {
        2 => app.Apks.Count > 0,
        3 => app.Obb == FolderState.Present,
        4 => app.Data == FolderState.Present || app.Debuggable,
        _ => false
    };

    private Bitmap IconFor(AppEntry app)
    {
        if (_icons.TryGetValue(app.Package, out Bitmap? cached))
            return cached;
        using SKBitmap? decoded = ImageLoader.Decode(app.Icon);
        using SKBitmap thumb = decoded is not null ? ImageLoader.Thumbnail(decoded, 80) : LetterIcon(app.Name, 80);   // 2× for HiDPI
        var result = new Bitmap(new MemoryStream(ImageLoader.EncodePng(thumb)));
        _icons[app.Package] = result;
        return result;
    }

    private static SKBitmap LetterIcon(string name, int size)
    {
        SKBitmap bmp = ImageLoader.NewBitmap(size, size);
        using var g = new SKCanvas(bmp);
        using var fill = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#26364d") };
        using var border = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#3a4c64"), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        var rect = new SKRoundRect(new SKRect(1, 1, size - 1, size - 1), size * 0.22f);
        g.DrawRoundRect(rect, fill);
        g.DrawRoundRect(rect, border);
        string letter = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
        using var font = new SKFont(SKTypeface.Default, size * 0.45f);
        using var text = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#a8b4c6") };
        font.MeasureText(letter, out SKRect bounds);
        g.DrawText(letter, (size - bounds.Width) / 2 - bounds.Left, (size - bounds.Height) / 2 - bounds.Top, font, text);
        return bmp;
    }

    private List<AppEntry> Sorted(IEnumerable<AppEntry> apps) => _sort.SelectedIndex == 0
        ? apps.OrderByDescending(a => a.InstallTime).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
        : apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Package, StringComparer.Ordinal).ToList();

    /// <summary>Re-sorts the list and keeps the ticked checkboxes.</summary>
    private void Resort()
    {
        if (_apps.Count == 0) return;
        var byApp = _rows.ToDictionary(r => r.App);
        _apps = Sorted(_apps);
        _rows = _apps.Select(a => byApp.TryGetValue(a, out AppRow? r) ? r : new AppRow(a)).ToList();
        FilterRows();
        UpdateCount();
    }

    private void Populate()
    {
        var old = _rows.ToDictionary(r => r.App);
        _rows = _apps.Select(a => old.TryGetValue(a, out AppRow? r) ? r : new AppRow(a)).ToList();
        FilterRows();
        UpdateCount();
    }

    private void FilterRows()
    {
        string query = (_search.Text ?? "").Trim();
        _list.ItemsSource = _rows.Where(r => query.Length == 0 ||
            $"{r.App.Name} {r.App.Version} {r.App.Package}".Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void SelectAll(bool selected)
    {
        foreach (AppRow row in _rows)
        {
            if (Available(row.App, 2)) row.Apk = selected;
            if (Available(row.App, 3)) row.Obb = selected;
            if (Available(row.App, 4)) row.Data = selected;
        }
        UpdateCount();
    }

    private List<BackupSelection> Selections() =>
        _rows.Select(r => new BackupSelection(r.App, r.Apk, r.Obb, r.Data)).Where(s => s.Apk || s.Obb || s.Data).ToList();

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
        _rows = new List<AppRow>();
        _scannedSerial = "";
        _list.ItemsSource = null;
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
        _abort.IsEnabled = false;
        SetStatus(Loc.T("Vorgang wird abgebrochen …", "Cancelling …"));
    }

    private bool _lastIdle = true;
    private void UpdateEnabled()
    {
        bool mine = _cts is not null;
        bool idle = !_state.IsBusy;
        _scan.IsEnabled = idle;
        _selectAll.IsEnabled = _selectNone.IsEnabled = idle && _apps.Count > 0;
        _destination.IsEnabled = _browse.IsEnabled = _bundleSplits.IsEnabled = idle;
        _backup.IsEnabled = idle && _apps.Count > 0;
        _abort.IsEnabled = mine && !_cts!.IsCancellationRequested;
        if (idle != _lastIdle)
        {
            _lastIdle = idle;
            FilterRows();   // rebuild rows: check boxes / trash enabled state
        }
    }

    private void SetStatus(string message)
    {
        _statusIsReady = false;
        _status.Text = message.Replace("\n", "  ");
    }

    private void OnMessage(string message)
    {
        SetStatus(message);
        Ui.AppendLine(_log, message);
    }

    private void OnProgress((int Current, int Total) p)
    {
        _progress.IsIndeterminate = false;
        _progress.Maximum = Math.Max(1, p.Total);
        _progress.Value = p.Current;
    }

    private async Task<AdbDevice?> ReadyDevice()
    {
        AdbDevice? device = _state.SelectedDevice;
        if (!_state.HasAdb)
        {
            await Dialogs.Message(Owner, Loc.T("ADB ist nicht eingerichtet. Bitte im Tab „Allgemeine Einstellungen“ ADB auswählen.",
                "ADB is not set up. Please choose ADB in the \"General settings\" tab."), Loc.T("ADB fehlt", "ADB missing"));
            return null;
        }
        if (device is null || !device.IsReady)
        {
            await Dialogs.Message(Owner, Loc.T(
                    "Bitte ein verbundenes, autorisiertes Gerät auswählen.\nBei ‚unauthorized‘ den RSA-Dialog auf dem Android-Gerät bestätigen und erneut suchen.",
                    "Select a connected, authorized device.\nIf it says 'unauthorized', accept the RSA prompt on Android and find devices again."),
                Loc.T("Gerät nicht bereit", "Device not ready"));
            return null;
        }
        return device;
    }

    private async Task ScanAsync()
    {
        AdbDevice? device = await ReadyDevice();
        if (device is null || !_state.TryBeginOperation(Loc.T("Apps werden eingelesen", "Scanning apps")))
            return;

        InvalidateScan();
        _cts = new CancellationTokenSource();
        _progress.IsIndeterminate = true;
        UpdateEnabled();

        var log = new Progress<string>(OnMessage);
        var progress = new Progress<(int, int)>(OnProgress);
        var service = new BackupService(new AdbClient(_state.AdbPath!, device.Serial),
            ((IProgress<string>)log).Report, (c, t) => ((IProgress<(int, int)>)progress).Report((c, t)), _cts.Token);

        try
        {
            List<AppEntry> apps = await Task.Run(() => service.ScanAsync(resolveNames: true));
            _icons.Clear();   // icons may have changed (re-patched / updated apps)
            _apps = Sorted(apps);
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
            await Dialogs.Message(Owner, ex.Message, Loc.T("Vorgang nicht abgeschlossen", "Operation incomplete"), MessageKind.Warning);
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
            await Dialogs.Message(Owner, Loc.T("Bitte mindestens ein Feld APK, OBB oder Daten auswählen.", "Select at least one APK, OBB or Data checkbox."),
                Loc.T("Keine Auswahl", "Nothing selected"));
            return;
        }

        AdbDevice? device = await ReadyDevice();
        if (device is null)
            return;
        if (device.Serial != _scannedSerial)
        {
            await Dialogs.Message(Owner, Loc.T("Die App-Liste gehört zu einem anderen Gerät. Bitte Apps erneut einlesen.",
                "The app list belongs to a different device. Please scan the apps again."), "ADBora");
            return;
        }

        string destinationText = Privacy.Expand((_destination.Text ?? "").Trim());
        if (destinationText.Length == 0)
        {
            await Dialogs.Message(Owner, Loc.T("Bitte einen Backup-Ordner wählen.", "Please choose a backup folder."),
                Loc.T("Speicherort fehlt", "Destination missing"), MessageKind.Warning);
            return;
        }

        string destination;
        try { destination = Path.GetFullPath(destinationText); }
        catch (Exception ex)
        {
            await Dialogs.Message(Owner, ex.Message, Loc.T("Speicherort ungültig", "Invalid destination"), MessageKind.Warning);
            return;
        }
        SaveDestination();

        // Games often write settings / save games only when they are quit – warn if a selected app is still running
        var withData = selections.Where(sel => sel.Data).ToList();
        if (withData.Count > 0)
        {
            var running = new List<string>();
            try
            {
                var probe = new AdbClient(_state.AdbPath!, device.Serial);
                string pids = string.Join("; ", withData.Select(sel => $"pidof {sel.App.Package} >/dev/null && echo RUN:{sel.App.Package}"));
                AdbResult r = await probe.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(15), "shell", pids);
                var lines = r.Output.Split('\n').Select(l => l.Trim()).ToHashSet();
                foreach (BackupSelection sel in withData)
                    if (lines.Contains("RUN:" + sel.App.Package))
                        running.Add(sel.App.Name);
            }
            catch (AdbException) { }
            if (running.Count > 0)
            {
                bool answer = await Dialogs.Message(Owner, Loc.T($"Diese App(s) laufen noch auf dem Headset:\n\n{string.Join("\n", running)}\n\nViele Spiele schreiben Einstellungen und Spielstände erst beim Beenden. Für ein vollständiges Backup das Spiel im Headset zuerst normal beenden (nicht nur absetzen).\n\nTrotzdem jetzt sichern?",
                          $"These app(s) are still running on the headset:\n\n{string.Join("\n", running)}\n\nMany games write settings and save games only when they are quit. For a complete backup, quit the game on the headset first (not just take it off).\n\nBack up anyway?"),
                    Loc.T("App läuft noch", "App still running"), MessageKind.Question, defaultNo: true);
                if (!answer)
                    return;
            }
        }

        if (!_state.TryBeginOperation(Loc.T("Backup läuft", "Backup running")))
            return;

        _cts = new CancellationTokenSource();
        _progress.IsIndeterminate = true;
        UpdateEnabled();

        var log = new Progress<string>(OnMessage);
        var progress = new Progress<(int, int)>(OnProgress);
        var service = new BackupService(new AdbClient(_state.AdbPath!, device.Serial),
            ((IProgress<string>)log).Report, (c, t) => ((IProgress<(int, int)>)progress).Report((c, t)), _cts.Token)
        {
            BundleSplits = _bundleSplits.IsChecked == true
        };

        try
        {
            BackupReport report = await Task.Run(() => service.BackupAsync(selections, destination, device.Model));
            _lastBackup = report.Directory;
            _openFolder.IsEnabled = true;

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
                await Dialogs.Message(Owner, message + Loc.T("\nDetails stehen im Backup-Bericht. .partial-Ordner sind unvollständig.", "\nSee the backup report for details. .partial folders are incomplete.") +
                    (details.Length > 0 ? "\n\n" + (details.Length > 2500 ? details[..2500] + " …" : details) : ""),
                    Loc.T("Backup unvollständig", "Backup incomplete"), MessageKind.Warning);
            }
            else if (skipped > 0)
            {
                string details = string.Join("\n", report.Apps.Where(a => a.Skipped.Count > 0)
                    .Select(a => $"{a.Name}: " + string.Join(", ", a.Skipped.Take(3).Select(Path.GetFileName)) + (a.Skipped.Count > 3 ? $" (+{a.Skipped.Count - 3})" : "")));
                await Dialogs.Message(Owner, message + "\n\n" + (details.Length > 2000 ? details[..2000] + " …" : details) +
                    Loc.T("\n\nDie vollständige Liste steht in manifest.json der App („skipped_unreadable“).", "\n\nThe full list is in the app's manifest.json (\"skipped_unreadable\")."),
                    Loc.T("Backup abgeschlossen (mit Hinweisen)", "Backup complete (with notes)"));
            }
            else
            {
                await Dialogs.Message(Owner, message, Loc.T("Backup abgeschlossen", "Backup complete"));
            }
        }
        catch (OperationCanceledException)
        {
            OnMessage(Loc.T("Vorgang abgebrochen.", "Operation cancelled."));
        }
        catch (Exception ex)
        {
            OnMessage(ex.Message);
            await Dialogs.Message(Owner, ex.Message, Loc.T("Vorgang nicht abgeschlossen", "Operation incomplete"), MessageKind.Warning);
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>Uninstalls one app from the scanned device (after confirmation).</summary>
    private async Task UninstallAsync(AppRow row)
    {
        AppEntry app = row.App;
        if (_state.IsBusy)
            return;
        AdbDevice? device = await ReadyDevice();
        if (device is null)
            return;
        if (device.Serial != _scannedSerial)
        {
            await Dialogs.Message(Owner, Loc.T("Die App-Liste gehört zu einem anderen Gerät. Bitte Apps erneut einlesen.",
                "The app list belongs to a different device. Please scan the apps again."), "ADBora");
            return;
        }

        string title = app.Name + (app.Version.Length > 0 ? " " + app.Version : "");
        if (await Dialogs.Message(Owner, Loc.T($"„{title}“ ({app.Package}) von {device.DisplayName} deinstallieren?\n\nDie App und ihre Daten auf dem Gerät werden entfernt. Das kann nicht rückgängig gemacht werden – vorher ggf. ein Backup erstellen.",
                      $"Uninstall \"{title}\" ({app.Package}) from {device.DisplayName}?\n\nThe app and its data on the device will be removed. This cannot be undone – create a backup first if needed."),
                Loc.T("App deinstallieren", "Uninstall app"), MessageKind.Question, defaultNo: true))
            return;

        if (!_state.TryBeginOperation(Loc.T("Deinstallation läuft", "Uninstalling")))
            return;
        _progress.IsIndeterminate = true;
        try
        {
            OnMessage(Loc.T($"Deinstalliere {title} ({app.Package}) …", $"Uninstalling {title} ({app.Package}) …"));
            AdbResult result = await new AdbClient(_state.AdbPath!, device.Serial)
                .CaptureAsync(CancellationToken.None, TimeSpan.FromMinutes(2), "uninstall", app.Package);
            if (result.Ok && result.Combined.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                OnMessage(Loc.T($"✔ {title} wurde deinstalliert.", $"✔ {title} was uninstalled."));
                _apps.Remove(app);
                _rows.Remove(row);
                FilterRows();
                UpdateCount();
            }
            else
            {
                string detail = result.Combined.Trim();
                OnMessage(Loc.T($"FEHLER · Deinstallation von {title}: {detail}", $"ERROR · Uninstalling {title}: {detail}"));
                await Dialogs.Message(Owner, Loc.T($"Deinstallation fehlgeschlagen:\n{detail}", $"Uninstall failed:\n{detail}"), "ADBora", MessageKind.Warning);
            }
        }
        catch (Exception ex)
        {
            OnMessage(ex.Message);
        }
        finally
        {
            _progress.IsIndeterminate = false;
            _state.EndOperation();
            UpdateEnabled();
        }
    }

    private void Finish()
    {
        _cts?.Dispose();
        _cts = null;
        _progress.IsIndeterminate = false;
        _state.EndOperation();
        UpdateEnabled();
    }

    private void SaveDestination()
    {
        _state.Settings.BackupDestination = (_destination.Text ?? "").Trim();
        _state.Settings.Save();
    }

    private async Task ChooseDestination()
    {
        if (Owner is not { } owner) return;
        string current = Privacy.Expand((_destination.Text ?? "").Trim());
        string? folder = await Dialogs.OpenFolder(owner, Loc.T("Backup-Speicherort", "Backup destination"), Directory.Exists(current) ? current : null);
        if (folder is not null)
        {
            _destination.Text = Privacy.ShowPath(folder);
            SaveDestination();
        }
    }
}

/// <summary>Small trash can icon (vector).</summary>
internal sealed class TrashIcon : Control
{
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Parent is Button b)
        {
            b.PointerEntered += (_, _) => InvalidateVisual();
            b.PointerExited += (_, _) => InvalidateVisual();
        }
    }

    public override void Render(DrawingContext g)
    {
        IBrush brush = (Parent as Button)?.IsPointerOver == true ? Brushes.White : AppTheme.Muted;
        var pen = new Pen(brush, 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        double s = Bounds.Width / 16;
        Point P(double x, double y) => new(x * s, y * s);
        g.DrawLine(pen, P(2, 4), P(14, 4));
        g.DrawLine(pen, P(6, 4), P(6.5, 2));
        g.DrawLine(pen, P(6.5, 2), P(9.5, 2));
        g.DrawLine(pen, P(9.5, 2), P(10, 4));
        var body = new StreamGeometry();
        using (var c = body.Open())
        {
            c.BeginFigure(P(3.5, 4), false);
            c.LineTo(P(4.5, 14.5)); c.LineTo(P(11.5, 14.5)); c.LineTo(P(12.5, 4));
            c.EndFigure(false);
        }
        g.DrawGeometry(null, pen, body);
        g.DrawLine(pen, P(6.7, 7), P(6.9, 12));
        g.DrawLine(pen, P(9.3, 7), P(9.1, 12));
    }
}
