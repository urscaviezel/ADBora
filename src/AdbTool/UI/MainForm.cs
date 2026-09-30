using AdbTool.Core;
using AdbTool.Tabs;

namespace AdbTool.UI;

internal interface IPage
{
    /// <summary>Called when the page becomes visible.</summary>
    void OnActivated();

    /// <summary>Cancels a running operation of this page (if any).</summary>
    void CancelOperation();
}

internal sealed class MainForm : Form
{
    private readonly AppState _state;
    private readonly TabStrip _tabs = new() { Dock = DockStyle.Top };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly DarkComboBox _device = Ui.Combo(380);
    private readonly DarkButton _refresh = Ui.Button("↻", "↻");
    private readonly Label _deviceDot = new() { AutoSize = true, Text = "●", Font = new Font("Segoe UI", 12f), BackColor = Color.Transparent };
    private readonly Label _busyLabel = new() { AutoSize = true, ForeColor = Theme.Warning, BackColor = Color.Transparent, Font = Theme.BaseFont };
    private readonly List<Control> _pages = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 4000 };
    private readonly bool _smokeTest;
    private bool _updatingCombo;
    private bool _closeRequested;
    private bool _closingDone;

    public MainForm(bool smokeTest = false)
    {
        _smokeTest = smokeTest;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _state = new AppState(AppSettings.Load());
        Loc.SetLanguage(_state.Settings.Language);

        Text = $"ADBora {AppInfo.Version}";
        try
        {
            if (Icon.ExtractAssociatedIcon(Application.ExecutablePath) is { } icon)
                Icon = icon;
        }
        catch { }

        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.BaseFont;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(620, 560);
        Size = new Size(Math.Max(620, _state.Settings.WindowWidth), Math.Max(560, _state.Settings.WindowHeight));
        if (_state.Settings.WindowMaximized)
            WindowState = FormWindowState.Maximized;

        BuildLayout();
        ResumeLayout(false);

        _state.DevicesChanged += UpdateDeviceCombo;
        _state.SelectedDeviceChanged += UpdateDeviceCombo;
        _state.BusyChanged += OnBusyChanged;
        _state.AdbChanged += async () => { await _state.RefreshVersionAsync(); await _state.RefreshDevicesAsync(); UpdateDeviceCombo(); };
        Loc.LanguageChanged += () => { UpdateDeviceCombo(); OnBusyChanged(); };

        _poll.Tick += async (_, _) =>
        {
            if (!_state.IsBusy && _state.HasAdb && WindowState != FormWindowState.Minimized)
                await _state.RefreshDevicesAsync();
        };

        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);
        Shown += async (_, _) => await OnFirstShownAsync();
        FormClosing += OnFormClosing;
    }

    private void BuildLayout()
    {
        // Header: title + global device selection
        var header = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.HeaderBackground, Padding = new Padding(20, 0, 20, 0) };
        var title = new BrandLabel { Location = new Point(18, 14) };
        var subtitle = new Label { AutoSize = true, ForeColor = Theme.Muted, BackColor = Color.Transparent, Font = Theme.BaseFont };
        subtitle.Text = "Android Device Toolbox";
        header.Controls.Add(title);
        header.Controls.Add(subtitle);

        var deviceRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        var deviceLabel = Ui.Label("Gerät", "Device", muted: true);
        deviceLabel.Margin = new Padding(0, 9, 8, 0);
        _deviceDot.Margin = new Padding(0, 5, 4, 0);
        _busyLabel.Margin = new Padding(0, 9, 14, 0);
        _device.Margin = new Padding(0, 4, 6, 0);
        _refresh.Margin = new Padding(0, 0, 0, 0);
        _refresh.MinimumSize = new Size(38, 34);
        var tip = new ToolTip();
        Loc.Bind(_refresh, t => tip.SetToolTip(_refresh, t), "Geräteliste aktualisieren", "Refresh device list");
        deviceRow.Controls.AddRange(new Control[] { _busyLabel, deviceLabel, _deviceDot, _device, _refresh });
        header.Controls.Add(deviceRow);

        bool positioning = false;
        void PositionHeader()
        {
            if (positioning) return;
            positioning = true;
            try
            {
                // Device combo shrinks with the window (between 180 and 380 px @96 dpi).
                int fixedPart = deviceRow.Width - _device.Width;
                int available = header.ClientSize.Width - title.Right - Theme.S(40) - fixedPart;
                int width = Math.Clamp(available, Theme.S(180), Theme.S(380));
                if (Math.Abs(width - _device.Width) > 2)
                {
                    _device.Width = width;
                    _device.DropDownWidth = Math.Max(width, Theme.S(380));
                }

                deviceRow.Location = new Point(header.ClientSize.Width - deviceRow.Width - Theme.S(20), (header.Height - deviceRow.Height) / 2);
                subtitle.Location = new Point(title.Right + Theme.S(12), title.Top + (title.Height - subtitle.Height) / 2 + Theme.S(2));
                subtitle.Visible = subtitle.Right + Theme.S(16) < deviceRow.Left;
            }
            finally
            {
                positioning = false;
            }
        }
        header.Resize += (_, _) => PositionHeader();
        deviceRow.SizeChanged += (_, _) => PositionHeader();
        title.SizeChanged += (_, _) => PositionHeader();
        subtitle.SizeChanged += (_, _) => PositionHeader();

        _device.SelectedIndexChanged += (_, _) =>
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

        _tabs.AddTab("\uE713", "Allgemeine Einstellungen", "General settings");
        _tabs.AddTab("\uE74E", "APK Backup", "APK backup");
        _tabs.AddTab("\uE896", "APK Install", "APK install");
        _tabs.AddTab("\uEC4A", "Speedtest", "Speed test");
        _tabs.AddTab("\uE756", "ADB Commands", "ADB commands");
        _tabs.SelectedIndexChanged += ShowPage;

        _pages.Add(new SettingsPage(_state));
        _pages.Add(new BackupPage(_state));
        _pages.Add(new InstallPage(_state));
        _pages.Add(new SpeedTestPage(_state));
        _pages.Add(new CommandsPage(_state));
        foreach (Control page in _pages)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _content.Controls.Add(page);
        }

        Controls.Add(_content);
        Controls.Add(_tabs);
        Controls.Add(header);

        int last = Math.Clamp(_state.Settings.LastTab, 0, _pages.Count - 1);
        _pages[0].Visible = last == 0;
        _tabs.SelectedIndex = last;
        if (last == 0) ShowPage(0);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

    /// <summary>
    /// Maximize / restore: Windows would repaint every intermediate layout
    /// step of all cards. Drawing is suspended while the new size is laid out
    /// and the window is painted once afterwards.
    /// </summary>
    /// <summary>
    /// WS_EX_COMPOSITED: Windows paints the whole window including all child
    /// controls bottom-up into one buffer — no flicker when opening,
    /// maximizing, restoring or resizing.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_MASK = 0xFFF0, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;

        if (m.Msg == WM_SYSCOMMAND)
        {
            int cmd = (int)m.WParam & SC_MASK;
            if (cmd is SC_MAXIMIZE or SC_RESTORE && WindowState != FormWindowState.Minimized)
            {
                bool frozen = Ui.BeginFreeze(this);
                SuspendLayout();
                try
                {
                    base.WndProc(ref m);
                }
                finally
                {
                    ResumeLayout(true);
                    Ui.EndFreeze(this, frozen);
                }
                return;
            }
        }
        base.WndProc(ref m);
    }

    private bool _resizing;

    protected override void OnResizeBegin(EventArgs e)
    {
        // Interactive resizing (dragging the border): lay out only at the end.
        _resizing = true;
        SuspendLayout();
        base.OnResizeBegin(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (!_resizing) return;
        _resizing = false;
        Ui.Frozen(this, () => ResumeLayout(true));
    }

    private void ShowPage(int index)
    {
        // Switch pages with drawing suspended, then repaint once.
        bool redrawOff = _content.IsHandleCreated;
        if (redrawOff) SendMessage(_content.Handle, 0x000B /* WM_SETREDRAW */, IntPtr.Zero, IntPtr.Zero);
        try
        {
            _pages[index].Visible = true;
            _pages[index].BringToFront();
            for (int i = 0; i < _pages.Count; i++)
                if (i != index) _pages[i].Visible = false;
        }
        finally
        {
            if (redrawOff)
            {
                SendMessage(_content.Handle, 0x000B, (IntPtr)1, IntPtr.Zero);
                // RDW_ERASE | RDW_FRAME | RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW
                RedrawWindow(_content.Handle, IntPtr.Zero, IntPtr.Zero, 0x0004 | 0x0400 | 0x0001 | 0x0080 | 0x0100);
            }
        }
        _state.Settings.LastTab = index;
        (_pages[index] as IPage)?.OnActivated();
    }

    private async Task OnFirstShownAsync()
    {
        UpdateDeviceCombo();
        OnBusyChanged();

        if (_smokeTest)
        {
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (_, _) => { t.Stop(); _closingDone = true; Close(); };
            t.Start();
            return;
        }

        if (!_state.HasAdb)
        {
            _tabs.SelectedIndex = 0;
            (_pages[0] as SettingsPage)?.ShowSetupHelp(missing: true);
        }
        else
        {
            await _state.RefreshVersionAsync();
            await _state.RefreshDevicesAsync();
            (_pages[0] as IPage)?.OnActivated();
        }

        _poll.Start();
    }

    private sealed record DeviceItem(AdbDevice Device)
    {
        public override string ToString() => $"{Device.DisplayName} · {Device.Serial} · {Device.StateText}";
    }

    private void UpdateDeviceCombo()
    {
        _updatingCombo = true;
        try
        {
            _device.BeginUpdate();
            _device.Items.Clear();
            foreach (AdbDevice d in _state.Devices)
                _device.Items.Add(new DeviceItem(d));
            if (_state.Devices.Count == 0)
            {
                _device.Items.Add(_state.HasAdb
                    ? Loc.T("Kein Gerät verbunden", "No device connected")
                    : Loc.T("ADB nicht eingerichtet", "ADB not set up"));
                _device.SelectedIndex = 0;
            }
            else
            {
                int index = _state.Devices.ToList().FindIndex(d => d.Serial == _state.SelectedDevice?.Serial);
                _device.SelectedIndex = Math.Max(0, index);
            }
            _device.EndUpdate();

            AdbDevice? sel = _state.SelectedDevice;
            _deviceDot.ForeColor = sel is null ? Theme.Error : sel.IsReady ? Theme.Accent : Theme.Warning;
        }
        finally
        {
            _updatingCombo = false;
        }
    }

    private void OnBusyChanged()
    {
        _device.Enabled = !_state.IsBusy;
        _refresh.Enabled = !_state.IsBusy;
        _busyLabel.Text = _state.IsBusy ? "⏳ " + _state.BusyOperation : "";
        _busyLabel.Visible = _state.IsBusy;

        if (!_state.IsBusy && _closeRequested)
            BeginInvoke(new Action(Close));
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_state.IsBusy && !_closingDone)
        {
            _closeRequested = true;
            e.Cancel = true;
            foreach (IPage page in _pages.OfType<IPage>())
                page.CancelOperation();
            return;
        }

        _poll.Stop();
        _state.Settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        if (WindowState == FormWindowState.Normal)
        {
            _state.Settings.WindowWidth = (int)(Width / Theme.Scale);
            _state.Settings.WindowHeight = (int)(Height / Theme.Scale);
        }
        _state.Settings.Save();

        if (_state.Settings.StopAdbServerOnExit && _state.Adb is { } adb && !_smokeTest)
        {
            try { adb.KillServerAsync().Wait(TimeSpan.FromSeconds(5)); } catch { }
        }
    }
}
