using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using AdbTool.Core;
using AdbTool.Tabs;

namespace AdbTool.UI;

internal sealed class MainWindow : Window
{
    public static WindowIcon? AppIcon { get; } = LoadIcon();

    private readonly AppState _state;
    private readonly TabStrip _tabs = new();
    private readonly Panel _content = new();
    private readonly ComboBox _device = new() { Width = 380, MinHeight = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _refresh = new() { Content = "↻", FontSize = 15, Padding = new Thickness(10, 2), MinWidth = 38, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _deviceDot = new() { Text = "●", FontSize = 15, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _busyLabel = new() { Foreground = AppTheme.Warning, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _subtitle = new() { Text = "Android Device Toolbox", Margin = new Thickness(12, 6, 0, 0) };
    private readonly List<Control> _pages = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly bool _smokeTest;
    private NetworkRelocator? _relocator;
    private bool _updatingCombo;
    private bool _closeRequested;
    private bool _closingDone;

    public MainWindow(bool smokeTest = false)
    {
        _smokeTest = smokeTest;
        _state = new AppState(AppSettings.Load());
        FirstStartLanguage(smokeTest);
        Loc.SetLanguage(_state.Settings.Language);

        Title = $"ADBora {AppInfo.DisplayVersion}";
        Icon = AppIcon;
        MinWidth = 620;
        MinHeight = 560;
        Width = Math.Max(620, _state.Settings.WindowWidth);
        Height = Math.Max(560, _state.Settings.WindowHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_state.Settings.WindowMaximized)
            WindowState = WindowState.Maximized;

        BuildLayout();

        _state.DevicesChanged += UpdateDeviceCombo;
        Privacy.Changed += UpdateDeviceCombo;
        _state.SelectedDeviceChanged += UpdateDeviceCombo;
        _state.BusyChanged += OnBusyChanged;
        _state.AdbChanged += async () => { await _state.RefreshVersionAsync(); await _state.RefreshDevicesAsync(); UpdateDeviceCombo(); };
        Loc.LanguageChanged += () => { UpdateDeviceCombo(); OnBusyChanged(); };

        _poll.Tick += async (_, _) =>
        {
            if (!_state.IsBusy && _state.HasAdb && WindowState != WindowState.Minimized)
                await _state.RefreshDevicesAsync();
        };

        Opened += async (_, _) => await OnFirstShownAsync();
        Closing += OnClosing;
    }

    private static WindowIcon? LoadIcon()
    {
        try { return new WindowIcon(AssetLoader.Open(new Uri("avares://ADBora/ADBora.ico"))); }
        catch { return null; }
    }

    private void BuildLayout()
    {
        // Header: title + global device selection
        _subtitle.Classes.Add("muted");
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { new BrandLabel(), _subtitle } };

        var deviceLabel = Ui.Label("Gerät", "Device", muted: true);
        deviceLabel.Margin = new Thickness(0, 0, 10, 0);
        Loc.Bind(_refresh, t => ToolTip.SetTip(_refresh, t), "Geräteliste aktualisieren", "Refresh device list");
        var deviceRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _busyLabel, deviceLabel, _deviceDot, _device, _refresh },
        };
        var header = new DockPanel { Background = AppTheme.HeaderBackground, Height = 62 };
        var headerInner = new Grid { Margin = new Thickness(20, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(deviceRow, 1);
        headerInner.Children.Add(brand);
        headerInner.Children.Add(deviceRow);
        header.Children.Add(headerInner);

        // Device combo shrinks with the window (between 180 and 380 px), subtitle hides when narrow
        headerInner.SizeChanged += (_, e) =>
        {
            double width = Math.Clamp(e.NewSize.Width - 520, 180, 380);
            _device.Width = width;
            _subtitle.IsVisible = e.NewSize.Width > 820;
        };

        _device.SelectionChanged += (_, _) =>
        {
            if (_updatingCombo) return;
            _state.SelectDevice(_device.SelectedItem is DeviceItem item ? item.Device : null);
        };
        _refresh.Click += async (_, _) =>
        {
            if (!_state.HasAdb)
            {
                _tabs.SelectedIndex = 0;
                return;
            }
            await _state.RefreshDevicesAsync();
        };

        _tabs.AddTab(TabGlyph.Settings, "Allgemeine Einstellungen", "General settings");
        _tabs.AddTab(TabGlyph.Backup, "APK Backup", "APK backup");
        _tabs.AddTab(TabGlyph.Install, "APK Install", "APK install");
        _tabs.AddTab(TabGlyph.Speed, "Speedtest", "Speed test");
        _tabs.AddTab(TabGlyph.Stream, "Stream (Beta)", "Stream (beta)");
        _tabs.AddTab(TabGlyph.Performance, "Performance", "Performance");
        _tabs.AddTab(TabGlyph.Console, "ADB Commands", "ADB commands");
        _tabs.AddTab(TabGlyph.Info, "Build Info", "Build info");
        _tabs.SelectedIndexChanged += ShowPage;

        _pages.Add(new SettingsPage(_state));
        _pages.Add(new PendingPage("APK Backup", "APK backup"));
        _pages.Add(new PendingPage("APK Install", "APK install"));
        _pages.Add(new PendingPage("Speedtest", "Speed test"));
        _pages.Add(new PendingPage("Stream (Beta)", "Stream (beta)"));
        _pages.Add(new PendingPage("Performance", "Performance"));
        _pages.Add(new PendingPage("ADB Commands", "ADB commands"));
        _pages.Add(new PendingPage("Build Info", "Build info"));
        foreach (Control page in _pages)
        {
            page.IsVisible = false;
            _content.Children.Add(page);
        }

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_tabs, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_tabs);
        root.Children.Add(_content);
        Content = root;

        string[] args = Environment.GetCommandLineArgs();
        int startTab = Array.IndexOf(args, "--start-tab");
        if (startTab > 0 && startTab + 1 < args.Length && int.TryParse(args[startTab + 1], out int forced))
            _state.Settings.LastTab = forced; // smoke test of every start tab
        _tabs.SelectedIndex = Math.Clamp(_state.Settings.LastTab, 0, _pages.Count - 1);
    }

    private void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Count; i++)
            _pages[i].IsVisible = i == index;
        _state.Settings.LastTab = index;
        if (IsVisible) (_pages[index] as IPage)?.OnActivated();
    }

    public SettingsPage Settings => (SettingsPage)_pages[0];

    private async Task OnFirstShownAsync()
    {
        UpdateDeviceCombo();
        OnBusyChanged();

        // Network devices whose IP changed (e.g. Wi-Fi → USB Ethernet adapter) are found again.
        _relocator = new NetworkRelocator(_state);
        _relocator.Status += text => { if (!_state.IsBusy) _busyLabel.Text = text ?? ""; };

        if (_smokeTest)
        {
            DispatcherTimer.RunOnce(() => { _closingDone = true; Close(); }, TimeSpan.FromMilliseconds(1500));
            return;
        }

        if (!_state.HasAdb)
        {
            _tabs.SelectedIndex = 0;
            await Settings.ShowSetupHelp(missing: true);
        }
        else
        {
            await _state.RefreshVersionAsync();
            await _state.RefreshDevicesAsync();
            (_pages[_tabs.SelectedIndex] as IPage)?.OnActivated();
        }

        _poll.Start();
    }

    /// <summary>
    /// First start in a language other than German/English: download that language from GitHub
    /// if it is not there yet; without internet ADBora starts in English.
    /// </summary>
    private void FirstStartLanguage(bool smokeTest)
    {
        AppSettings settings = _state.Settings;
        if (!settings.FirstStart) return;
        string code = settings.Language;
        if (code is not ("de" or "en") && Languages.Load(code) is null)
        {
            bool ok = false;
            if (!smokeTest && Languages.IsValidCode(code))
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    ok = Task.Run(() => Languages.DownloadAsync(code, cts.Token)).GetAwaiter().GetResult();
                }
                catch { ok = false; }
            }
            if (!ok) settings.Language = "en";
        }
        if (!smokeTest) settings.Save();
    }

    /// <summary>Closes ADBora without questions so that the update can replace it.</summary>
    public void ExitForUpdate()
    {
        foreach (IPage page in _pages.OfType<IPage>())
            page.CancelOperation();
        _closingDone = true;
        Dispatcher.UIThread.Post(Close);
    }

    private sealed record DeviceItem(AdbDevice Device)
    {
        public override string ToString() => $"{Device.DisplayName} · {Device.ShownSerial} · {Device.StateText}";
    }

    private void UpdateDeviceCombo()
    {
        _updatingCombo = true;
        try
        {
            var items = new List<object>();
            foreach (AdbDevice d in _state.Devices)
                items.Add(new DeviceItem(d));
            if (items.Count == 0)
                items.Add(_state.HasAdb ? Loc.T("Kein Gerät verbunden", "No device connected") : Loc.T("ADB nicht eingerichtet", "ADB not set up"));
            _device.ItemsSource = items;
            int index = _state.Devices.ToList().FindIndex(d => d.Serial == _state.SelectedDevice?.Serial);
            _device.SelectedIndex = Math.Max(0, index);

            AdbDevice? sel = _state.SelectedDevice;
            _deviceDot.Foreground = sel is null ? AppTheme.Error : sel.IsReady ? AppTheme.Accent : AppTheme.Warning;
        }
        finally
        {
            _updatingCombo = false;
        }
    }

    private void OnBusyChanged()
    {
        _device.IsEnabled = !_state.IsBusy;
        _refresh.IsEnabled = !_state.IsBusy;
        _busyLabel.Text = _state.IsBusy ? "⏳ " + _state.BusyOperation : "";
        _busyLabel.IsVisible = _state.IsBusy;

        if (!_state.IsBusy && _closeRequested)
            Dispatcher.UIThread.Post(Close);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_state.IsBusy && !_closingDone)
        {
            e.Cancel = true;
            if (_closeRequested) return;
            if (!e.IsProgrammatic)
            {
                bool yes = await Dialogs.Message(this,
                    Loc.T($"Es läuft noch ein Vorgang: {_state.BusyOperation}.\n\nWenn ADBora jetzt beendet wird, wird der Vorgang abgebrochen und bleibt unvollständig (z. B. ein unvollständiges Backup).\n\nADBora trotzdem beenden?",
                          $"An operation is still running: {_state.BusyOperation}.\n\nIf ADBora is closed now, the operation is cancelled and remains incomplete (e.g. an incomplete backup).\n\nClose ADBora anyway?"),
                    "ADBora", MessageKind.Question, defaultNo: true);
                if (!yes) return;
            }
            _closeRequested = true;
            foreach (IPage page in _pages.OfType<IPage>())
                page.CancelOperation();
            return;
        }

        _poll.Stop();
        _state.Settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _state.Settings.WindowWidth = (int)Width;
            _state.Settings.WindowHeight = (int)Height;
        }
        if (!_smokeTest)
            _state.Settings.Save();

        if (_state.Settings.StopAdbServerOnExit && _state.Adb is { } adb && !_smokeTest)
        {
            try { adb.KillServerAsync().Wait(TimeSpan.FromSeconds(5)); } catch { }
        }
    }
}

/// <summary>Placeholder for tabs that are not ported to Avalonia yet.</summary>
internal sealed class PendingPage : Border
{
    public PendingPage(string de, string en)
    {
        Padding = new Thickness(20, 16);
        var title = Ui.Label(de, en);
        title.Classes.Add("section");
        var card = new Card();
        card.SetContent(title);
        card.SetContent(Ui.Label("Dieser Tab wird gerade auf Avalonia portiert.", "This tab is being ported to Avalonia.", muted: true));
        card.VerticalAlignment = VerticalAlignment.Top;
        Child = card;
    }
}
