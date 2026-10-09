using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>Tab 4: free ADB command input with saved commands and history.</summary>
internal sealed class CommandsPage : UserControl, IPage
{
    private static readonly HashSet<string> HostCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "devices", "version", "start-server", "kill-server", "connect", "disconnect", "pair",
        "mdns", "help", "keygen", "host-features", "server-status"
    };

    private static readonly HashSet<string> GlobalOptions = new(StringComparer.Ordinal)
    {
        "-s", "-d", "-e", "-t", "-H", "-P", "-L", "-a"
    };

    private readonly AppState _state;
    private readonly DarkComboBox _saved = Ui.Combo(420);
    private readonly DarkButton _savedLoad = Ui.Button("Übernehmen", "Load");
    private readonly DarkButton _savedRun = Ui.Button("Ausführen", "Run");
    private readonly DarkButton _savedAdd = Ui.Button("Aktuellen Befehl speichern …", "Save current command …");
    private readonly DarkButton _savedDelete = Ui.Button("Löschen", "Delete");

    private readonly DarkComboBox _presets = Ui.Combo(420);
    private readonly Label _detected = Ui.Value("");
    private readonly Label _presetInfo = Ui.Value("");
    private readonly DarkButton _presetLoad = Ui.Button("Übernehmen", "Load");
    private readonly DarkButton _presetRun = Ui.Button("Ausführen", "Run");
    private readonly DarkButton _presetSave = Ui.Button("Zu eigenen Befehlen …", "Add to own commands …");
    private readonly DarkButton _explore = Ui.Button("Entdecken …", "Explore …");
    private readonly Dictionary<string, DeviceProfile> _profiles = new();
    private DeviceProfile? _profile;
    private string? _riskCommand;
    private CommandRisk _risk;

    private readonly TextBox _input = Ui.TextBox(600);
    private readonly CheckBox _targetDevice = Ui.CheckBox("An aktives Gerät senden (-s)", "Send to active device (-s)");
    private readonly DarkButton _run = Ui.Button("Ausführen  ⏎", "Run  ⏎", ButtonKind.Primary);
    private readonly DarkButton _stop = Ui.Button("Stopp", "Stop", ButtonKind.Danger);
    private readonly DarkButton _clear = Ui.Button("Ausgabe leeren", "Clear output");
    private readonly DarkButton _copy = Ui.Button("Ausgabe kopieren", "Copy output");
    private readonly RichTextBox _output = new();
    private readonly Label _status = Ui.Value("");

    private readonly ConcurrentQueue<(string Text, Color Color)> _pending = new();
    private readonly System.Windows.Forms.Timer _flush = new() { Interval = 80 };

    private Process? _process;
    private bool _stopRequested;
    private int _historyIndex = -1;

    public CommandsPage(AppState state)
    {
        _state = state;
        BackColor = Theme.Background;
        DoubleBuffered = true;
        Padding = new Padding(20, 16, 20, 16);

        BuildUi();
        ReloadSaved();

        _flush.Tick += (_, _) => FlushOutput();
        _flush.Start();
        _state.BusyChanged += UpdateEnabled;
        _state.SelectedDeviceChanged += UpdateEnabled;
        _state.SelectedDeviceChanged += () => _ = RefreshProfileAsync();
        _state.AdbChanged += UpdateEnabled;
        Loc.LanguageChanged += UpdateEnabled;
        Loc.LanguageChanged += () => { ReloadPresets(); ReloadSaved(); };
        ReloadPresets();
        UpdateEnabled();
    }

    private void BuildUi()
    {

        // Saved commands ------------------------------------------------------
        var savedCard = new Card("Gespeicherte Befehle", "Saved commands") { Dock = DockStyle.Top };
        var savedBox = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Color.Transparent, Dock = DockStyle.Top };
        savedBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _saved.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _saved.Margin = new Padding(0, 0, 0, 8);
        _saved.DropDownWidth = 700;
        _saved.SelectionChangeCommitted += (_, _) => LoadSelected();
        _savedLoad.Click += (_, _) => LoadSelected();
        _savedRun.Click += async (_, _) => { LoadSelected(); await RunAsync(); };
        _savedAdd.Click += (_, _) => SaveCurrent();
        _savedDelete.Click += (_, _) => DeleteSelected();
        var savedButtons = Ui.Row(_savedLoad, _savedRun, _savedAdd, _savedDelete);
        savedBox.Controls.Add(_saved, 0, 0);
        savedBox.Controls.Add(savedButtons, 0, 1);
        savedCard.SetContent(savedBox);

        // Device commands ---------------------------------------------------------
        var presetCard = new Card("Befehle für dieses Gerät", "Commands for this device") { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(0, 0, 10, 0) };
        var presetBox = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Color.Transparent, Dock = DockStyle.Top };
        presetBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _detected.ForeColor = Theme.Muted;
        _detected.Margin = new Padding(0, 0, 0, 6);
        Ui.WrapTo(_detected, presetCard);
        _presets.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _presets.Margin = new Padding(0, 0, 0, 6);
        _presets.DropDownWidth = 700;
        _presets.SelectionChangeCommitted += (_, _) => ShowPresetInfo();
        _presetInfo.Margin = new Padding(0, 0, 0, 8);
        _presetInfo.MinimumSize = new Size(0, 40);
        Ui.WrapTo(_presetInfo, presetCard);
        _presetLoad.Click += (_, _) => LoadPreset();
        _presetRun.Click += async (_, _) => { if (LoadPreset()) await RunAsync(); };
        _presetSave.Click += (_, _) => { if (_presets.SelectedItem is PresetItem p) SaveCommand(p.Preset.Command, p.Preset.Name); };
        _explore.Click += async (_, _) => await ExploreAsync();
        presetBox.Controls.Add(_detected, 0, 0);
        presetBox.Controls.Add(_presets, 0, 1);
        presetBox.Controls.Add(_presetInfo, 0, 2);
        presetBox.Controls.Add(Ui.Row(_presetLoad, _presetRun, _presetSave, _explore), 0, 3);
        presetCard.SetContent(presetBox);

        savedCard.Dock = DockStyle.None;
        savedCard.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        savedCard.Margin = new Padding(10, 0, 0, 0);
        var topRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        topRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topRow.Controls.Add(presetCard, 0, 0);
        topRow.Controls.Add(savedCard, 1, 0);
        Ui.Responsive(this, topRow, presetCard, savedCard, breakpoint: 1000, changed: narrow =>
        {
            presetCard.Margin = narrow ? new Padding(0, 0, 0, Theme.S(12)) : new Padding(0, 0, Theme.S(10), 0);
            savedCard.Margin = narrow ? new Padding(0) : new Padding(Theme.S(10), 0, 0, 0);
        });

        // Input -----------------------------------------------------------------
        var inputCard = new Card("Befehl", "Command") { Dock = DockStyle.Top };
        var inputGrid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Dock = DockStyle.Top };
        inputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var prefix = new Label { Text = "adb", AutoSize = true, Font = Theme.MonoFont, ForeColor = Theme.Accent, Margin = new Padding(0, 9, 6, 0), BackColor = Color.Transparent };
        _input.Font = Theme.MonoFont;
        _input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _input.Margin = new Padding(0, 4, 0, 4);
        Ui.Placeholder(_input, "z. B. shell getprop ro.product.model   ·   ↑/↓ = Verlauf", "e.g. shell getprop ro.product.model   ·   ↑/↓ = history");
        _input.KeyDown += OnInputKeyDown;
        inputGrid.Controls.Add(prefix, 0, 0);
        inputGrid.Controls.Add(_input, 1, 0);

        _targetDevice.Checked = _state.Settings.CommandTargetSelectedDevice;
        _targetDevice.CheckedChanged += (_, _) =>
        {
            _state.Settings.CommandTargetSelectedDevice = _targetDevice.Checked;
            _state.Settings.Save();
        };
        _run.Click += async (_, _) => await RunAsync();
        _stop.Click += (_, _) => CancelOperation();
        _clear.Click += (_, _) => _output.Clear();
        _copy.Click += (_, _) => { try { if (_output.TextLength > 0) Clipboard.SetText(_output.Text); } catch { } };
        var actions = Ui.Row(_run, _stop, _clear, _copy);
        actions.Margin = new Padding(0, 8, 0, 0);
        inputGrid.Controls.Add(actions, 0, 1);
        inputGrid.SetColumnSpan(actions, 2);
        _targetDevice.Margin = new Padding(0, 8, 0, 0);
        inputGrid.Controls.Add(_targetDevice, 0, 2);
        inputGrid.SetColumnSpan(_targetDevice, 2);

        var hint = Ui.Label(
            "Das Präfix „adb“ ist optional. Bei „shell …“ wird der Rest der Zeile unverändert an die Geräte-Shell übergeben (Pipes, Anführungszeichen). " +
            "Interaktive Befehle ohne Argumente (z. B. nur „shell“) werden nicht unterstützt; Dauerläufer wie „logcat“ mit Stopp beenden.",
            "The \"adb\" prefix is optional. With \"shell …\" the rest of the line is passed unchanged to the device shell (pipes, quotes). " +
            "Interactive commands without arguments (e.g. just \"shell\") are not supported; stop long-running commands such as \"logcat\" with Stop.",
            muted: true);
        Ui.WrapTo(hint, inputCard);
        hint.Margin = new Padding(0, 8, 0, 0);
        inputGrid.Controls.Add(hint, 0, 3);
        inputGrid.SetColumnSpan(hint, 2);
        inputCard.SetContent(inputGrid);

        // Output ----------------------------------------------------------------
        var outputCard = new Card("Ausgabe", "Output") { Dock = DockStyle.Fill, Margin = new Padding(0) };
        var outputBox = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _output.Dock = DockStyle.Fill;
        _output.ReadOnly = true;
        _output.BackColor = Theme.Console;
        _output.ForeColor = Theme.Text;
        _output.BorderStyle = BorderStyle.None;
        _output.Font = Theme.MonoFont;
        _output.WordWrap = false;
        _output.DetectUrls = false;
        _output.HideSelection = false;
        Theme.UseDarkScrollBars(_output);
        outputBox.Controls.Add(_output);
        _status.ForeColor = Theme.Muted;
        _status.AutoSize = false;
        _status.Dock = DockStyle.Bottom;
        _status.Height = 28;
        _status.TextAlign = ContentAlignment.BottomLeft;
        outputBox.Controls.Add(_status);
        outputCard.SetContent(outputBox, fill: true);
        // Docking: the fill control is added first, then the top controls (last added = topmost).
        Controls.Add(outputCard);
        Controls.Add(Spacer());
        Controls.Add(inputCard);
        Controls.Add(Spacer());
        Controls.Add(topRow);
    }

    private static Panel Spacer() => new() { Dock = DockStyle.Top, Height = 12, BackColor = Color.Transparent };

    // ------------------------------------------------------------------
    // Saved commands
    // ------------------------------------------------------------------

    private sealed record SavedItem(SavedCommand Command)
    {
        public override string ToString() =>
            $"{Command.Name}{(Command.Model.Length > 0 ? $"  [{Command.Model}]" : "")}   —   adb {Command.Command}";
    }

    private sealed record PresetItem(CommandPreset Preset)
    {
        public override string ToString() =>
            $"{Preset.Category} · {Preset.Name}{(Preset.Risk == CommandRisk.Dangerous ? "  ⚠" : Preset.Risk == CommandRisk.Caution ? "  !" : "")}";
    }

    private string CurrentModel => _state.SelectedDevice?.Model ?? "";

    // ------------------------------------------------------------------
    // Device specific command packs
    // ------------------------------------------------------------------

    private async Task RefreshProfileAsync()
    {
        AdbDevice? device = _state.SelectedDevice;
        ReloadSaved();
        if (device is null || !device.IsReady || !_state.HasAdb)
        {
            _profile = null;
            ReloadPresets();
            return;
        }
        if (!_profiles.TryGetValue(device.Serial, out DeviceProfile? profile))
        {
            try
            {
                var adb = new AdbClient(_state.AdbPath!, device.Serial);
                AdbResult r = await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "shell",
                    "getprop ro.product.manufacturer; getprop ro.product.brand; getprop ro.product.model; getprop ro.build.version.release");
                string[] l = r.Output.Replace("\r", "").Split('\n');
                string Get(int i) => i < l.Length ? l[i].Trim() : "";
                profile = new DeviceProfile(device.Serial, Get(0), Get(1), Get(2), Get(3), DeviceProfile.Detect(Get(0), Get(1), Get(2)));
                _profiles[device.Serial] = profile;
            }
            catch
            {
                profile = null;
            }
        }
        if (_state.SelectedDevice?.Serial != device.Serial) return; // device changed meanwhile
        _profile = profile;
        ReloadPresets();
    }

    private void ReloadPresets()
    {
        string? previous = (_presets.SelectedItem as PresetItem)?.Preset.Command;
        _presets.BeginUpdate();
        _presets.Items.Clear();
        foreach (CommandPreset p in CommandCatalog.For(_profile?.Family)
                     .OrderBy(p => p.Family is null ? 1 : 0)
                     .ThenBy(p => p.Category, StringComparer.CurrentCultureIgnoreCase))
            _presets.Items.Add(new PresetItem(p));
        _presets.EndUpdate();
        int index = previous is null ? 0 : Math.Max(0, _presets.Items.Cast<PresetItem>().ToList().FindIndex(i => i.Preset.Command == previous));
        if (_presets.Items.Count > 0) _presets.SelectedIndex = index;

        _detected.Text = _profile is { } pr
            ? Loc.T($"Erkannt: {pr.Manufacturer} {pr.Model} · Android {pr.Android} → Befehle für {pr.FamilyText}",
                    $"Detected: {pr.Manufacturer} {pr.Model} · Android {pr.Android} → commands for {pr.FamilyText}")
            : Loc.T("Kein bereites Gerät – allgemeine Android-Befehle.", "No ready device – general Android commands.");
        ShowPresetInfo();
    }

    private void ShowPresetInfo()
    {
        if (_presets.SelectedItem is not PresetItem item)
        {
            _presetInfo.Text = "";
            return;
        }
        CommandPreset p = item.Preset;
        string risk = p.Risk switch
        {
            CommandRisk.Dangerous => Loc.T("⚠ Vorsicht: ", "⚠ Caution: "),
            CommandRisk.Caution => Loc.T("! Hinweis: ", "! Note: "),
            _ => ""
        };
        _presetInfo.Text = $"{risk}{p.Description}\nadb {p.Command}";
        _presetInfo.ForeColor = p.Risk == CommandRisk.Dangerous ? Theme.Error : p.Risk == CommandRisk.Caution ? Theme.Warning : Theme.Muted;
        UpdateEnabled();
    }

    /// <summary>Puts the selected preset into the input; false if it still needs a value (placeholder).</summary>
    private bool LoadPreset()
    {
        if (_presets.SelectedItem is not PresetItem item) return false;
        SetInput(item.Preset.Command, item.Preset.Risk);
        return !item.Preset.NeedsInput;
    }

    private void SetInput(string command, CommandRisk risk)
    {
        _input.Text = command;
        _riskCommand = risk == CommandRisk.Safe ? null : command;
        _risk = risk;
        int ph = command.IndexOf('<');
        int end = ph >= 0 ? command.IndexOf('>', ph) : -1;
        _input.Focus();
        if (end > ph)
            _input.Select(ph, end - ph + 1);
        else
            _input.SelectionStart = _input.TextLength;
    }

    private async Task ExploreAsync()
    {
        if (_state.SelectedDevice is not { IsReady: true } device || !_state.HasAdb) return;
        CommandExplorer.Result? result = CommandExplorer.Show(FindForm(), _state, device);
        await Task.Yield();
        if (result is null) return;
        if (result.Save)
            SaveCommand(result.Command, "");
        else
            SetInput(result.Command, result.Risk);
    }

    private void ReloadSaved(string? select = null)
    {
        _saved.BeginUpdate();
        _saved.Items.Clear();
        string model = CurrentModel;
        foreach (SavedCommand cmd in _state.Settings.SavedCommands
                     .Where(c => c.Model.Length == 0 || string.Equals(c.Model, model, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            _saved.Items.Add(new SavedItem(cmd));
        _saved.EndUpdate();

        if (_saved.Items.Count > 0)
        {
            int index = select is null ? 0 : Math.Max(0, _saved.Items.Cast<SavedItem>().ToList().FindIndex(i => i.Command.Name == select));
            _saved.SelectedIndex = index;
        }
        UpdateEnabled();
    }

    private void LoadSelected()
    {
        if (_saved.SelectedItem is SavedItem item)
        {
            SetInput(item.Command.Command, CommandRisk.Safe);
        }
    }

    private void SaveCurrent()
    {
        string command = StripAdbPrefix(_input.Text.Trim());
        if (command.Length == 0)
        {
            MessageBox.Show(FindForm(), Loc.T("Bitte zuerst einen Befehl eingeben.", "Please enter a command first."), "ADBora");
            return;
        }
        string suggestion = _saved.SelectedItem is SavedItem item && item.Command.Command == command ? item.Command.Name : "";
        SaveCommand(command, suggestion);
    }

    private void SaveCommand(string command, string suggestion)
    {
        command = StripAdbPrefix(command.Trim());
        if (command.Length == 0) return;
        string model = CurrentModel;
        string? name = Prompt(Loc.T("Befehl speichern", "Save command"), Loc.T("Name für diesen Befehl:", "Name for this command:"), suggestion,
            model, out bool onlyModel);
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = name.Trim();

        SavedCommand? existing = _state.Settings.SavedCommands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null)
        {
            if (MessageBox.Show(FindForm(), Loc.T($"„{name}“ existiert bereits. Überschreiben?", $"\"{name}\" already exists. Overwrite?"),
                    "ADBora", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            existing.Command = command;
            existing.Model = onlyModel ? model : "";
        }
        else
        {
            _state.Settings.SavedCommands.Add(new SavedCommand { Name = name, Command = command, Model = onlyModel ? model : "" });
        }
        _state.Settings.Save();
        ReloadSaved(name);
    }

    private void DeleteSelected()
    {
        if (_saved.SelectedItem is not SavedItem item)
            return;
        if (MessageBox.Show(FindForm(), Loc.T($"Gespeicherten Befehl „{item.Command.Name}“ löschen?", $"Delete saved command \"{item.Command.Name}\"?"),
                "ADBora", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _state.Settings.SavedCommands.Remove(item.Command);
        _state.Settings.Save();
        ReloadSaved();
    }

    private string? Prompt(string title, string label, string value, string model, out bool onlyModel)
    {
        using Form dialog = Ui.Dialog(title, 520, model.Length > 0 ? 210 : 170);
        dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
        dialog.Padding = new Padding(Theme.S(18));
        var text = new Label { Text = label, Dock = DockStyle.Top, Height = Theme.S(28) };
        var box = Ui.TextBox(Theme.S(440));
        box.Text = value;
        box.Dock = DockStyle.Top;
        var ok = Ui.Button("Speichern", "Save", ButtonKind.Primary);
        var cancel = Ui.Button("Abbrechen", "Cancel");
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.Transparent };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        var modelOnly = Ui.CheckBox($"Nur für dieses Gerätemodell anzeigen ({model})", $"Show only for this device model ({model})");
        modelOnly.Dock = DockStyle.Top;
        modelOnly.Visible = model.Length > 0;
        dialog.Controls.Add(modelOnly);
        dialog.Controls.Add(box);
        dialog.Controls.Add(text);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        bool accepted = Ui.ShowDialog(dialog, FindForm()) == DialogResult.OK;
        onlyModel = model.Length > 0 && modelOnly.Checked;
        return accepted ? box.Text : null;
    }

    // ------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------

    public void OnActivated()
    {
        if (_state.SelectedDevice is { } d && (_profile is null || _profile.Serial != d.Serial))
            _ = RefreshProfileAsync();
        UpdateEnabled();
        _input.Focus();
    }

    public void CancelOperation()
    {
        if (_process is null) return;
        _stopRequested = true;
        AdbClient.KillQuietly(_process);
    }

    private void UpdateEnabled()
    {
        bool running = _process is not null;
        bool idle = !_state.IsBusy;
        _run.Enabled = idle && _state.HasAdb;
        _savedRun.Enabled = idle && _state.HasAdb && _saved.SelectedItem is not null;
        _savedLoad.Enabled = _savedDelete.Enabled = _saved.SelectedItem is not null;
        _stop.Enabled = running;
        _targetDevice.Enabled = !running;
        bool ready = _state.HasAdb && _state.SelectedDevice is { IsReady: true };
        bool hasPreset = _presets.SelectedItem is PresetItem;
        _presetLoad.Enabled = _presetSave.Enabled = hasPreset;
        _presetRun.Enabled = hasPreset && idle && ready && !((PresetItem)_presets.SelectedItem!).Preset.NeedsInput;
        _explore.Enabled = idle && ready;

        if (!running)
        {
            _status.Text = !_state.HasAdb
                ? Loc.T("ADB ist nicht eingerichtet (Tab „Allgemeine Einstellungen“).", "ADB is not set up (\"General settings\" tab).")
                : _state.IsBusy
                    ? Loc.T("Ein anderer ADB-Vorgang läuft gerade.", "Another ADB operation is running.")
                    : _state.SelectedDevice is { } d
                        ? Loc.T($"Aktives Gerät: {d.DisplayName} ({d.Serial})", $"Active device: {d.DisplayName} ({d.Serial})")
                        : Loc.T("Kein Gerät ausgewählt – nur Host-Befehle (z. B. devices, version) sinnvoll.", "No device selected – only host commands (e.g. devices, version) make sense.");
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        List<string> history = _state.Settings.CommandHistory;
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            if (_run.Enabled) _ = RunAsync();
        }
        else if (e.KeyCode == Keys.Up && history.Count > 0)
        {
            e.SuppressKeyPress = true;
            _historyIndex = _historyIndex < 0 ? history.Count - 1 : Math.Max(0, _historyIndex - 1);
            _input.Text = history[_historyIndex];
            _input.SelectionStart = _input.TextLength;
        }
        else if (e.KeyCode == Keys.Down && history.Count > 0)
        {
            e.SuppressKeyPress = true;
            if (_historyIndex < 0) return;
            _historyIndex++;
            if (_historyIndex >= history.Count)
            {
                _historyIndex = -1;
                _input.Clear();
            }
            else
            {
                _input.Text = history[_historyIndex];
                _input.SelectionStart = _input.TextLength;
            }
        }
    }

    private static string StripAdbPrefix(string line)
    {
        foreach (string prefix in new[] { "adb.exe ", "adb " })
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line[prefix.Length..].TrimStart();
        return line.Equals("adb", StringComparison.OrdinalIgnoreCase) ? "" : line;
    }

    /// <summary>Splits a command line; returns each token and the index after it in the source text.</summary>
    private static List<(string Token, int End)> Tokenize(string line)
    {
        var tokens = new List<(string, int)>();
        var current = new StringBuilder();
        bool inToken = false;
        char quote = '\0';

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add((current.ToString(), i));
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }
        if (inToken)
            tokens.Add((current.ToString(), line.Length));
        return tokens;
    }

    /// <summary>Builds the adb argument list for a user command line.</summary>
    private List<string>? BuildArguments(string line, out string? error)
    {
        error = null;
        List<(string Token, int End)> tokens = Tokenize(line);
        if (tokens.Count == 0)
            return null;

        var args = new List<string>();
        int i = 0;
        bool hasTarget = false;

        // Global options before the command (-s SERIAL, -d, -e, -t ID, -H, -P, -L …)
        while (i < tokens.Count && GlobalOptions.Contains(tokens[i].Token))
        {
            string opt = tokens[i].Token;
            args.Add(opt);
            if (opt is "-s" or "-d" or "-e" or "-t") hasTarget = true;
            if (opt is "-s" or "-t" or "-H" or "-P" or "-L" && i + 1 < tokens.Count)
                args.Add(tokens[++i].Token);
            i++;
        }

        if (i >= tokens.Count)
        {
            error = Loc.T("Kein Befehl angegeben.", "No command given.");
            return null;
        }

        string command = tokens[i].Token;
        if (!hasTarget && _targetDevice.Checked && _state.SelectedDevice is { } device && !HostCommands.Contains(command))
            args.InsertRange(0, new[] { "-s", device.Serial });

        args.Add(command);
        if (command is "shell" or "exec-out")
        {
            // Pass the rest of the line verbatim so pipes/quotes reach the device shell.
            int restStart = tokens[i].End;
            string rest = line[restStart..].Trim();
            // Options of "adb shell" itself (-t, -T, -x, -n) stay separate.
            if (rest.Length == 0)
            {
                error = Loc.T("Interaktive Shell wird nicht unterstützt – bitte einen Befehl nach „shell“ angeben, z. B. shell ls /sdcard.",
                    "Interactive shell is not supported – please add a command after \"shell\", e.g. shell ls /sdcard.");
                return null;
            }
            args.Add(rest);
        }
        else
        {
            for (int j = i + 1; j < tokens.Count; j++)
                args.Add(tokens[j].Token);
        }
        return args;
    }

    private async Task RunAsync()
    {
        string line = StripAdbPrefix(_input.Text.Trim());
        if (line.Length == 0 || _process is not null || !_state.HasAdb)
            return;

        if (line.Contains('<') && line.Contains('>') && System.Text.RegularExpressions.Regex.IsMatch(line, @"<[^<>\s|]+>"))
        {
            Enqueue(Loc.T("Bitte zuerst den Platzhalter <…> im Befehl ersetzen.\n", "Please replace the placeholder <…> in the command first.\n"), Theme.Warning);
            return;
        }

        if (_riskCommand is not null && line == StripAdbPrefix(_riskCommand) && _risk != CommandRisk.Safe)
        {
            string question = _risk == CommandRisk.Dangerous
                ? Loc.T($"Dieser Befehl kann Daten löschen oder das Gerät in einen besonderen Modus versetzen:\n\nadb {line}\n\nWirklich ausführen?",
                        $"This command can delete data or put the device into a special mode:\n\nadb {line}\n\nReally run it?")
                : Loc.T($"Dieser Befehl ändert das Verhalten des Geräts:\n\nadb {line}\n\nAusführen?",
                        $"This command changes the device behaviour:\n\nadb {line}\n\nRun it?");
            if (MessageBox.Show(FindForm(), question, "ADBora", MessageBoxButtons.YesNo,
                    _risk == CommandRisk.Dangerous ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
        }

        List<string>? args = BuildArguments(line, out string? error);
        if (args is null)
        {
            if (error is not null) Enqueue(error + "\n", Theme.Warning);
            return;
        }

        if (!_state.TryBeginOperation(Loc.T("ADB-Befehl läuft", "ADB command running")))
            return;

        // History (without duplicates in a row), max. 100 entries.
        List<string> history = _state.Settings.CommandHistory;
        history.Remove(line);
        history.Add(line);
        if (history.Count > 100) history.RemoveRange(0, history.Count - 100);
        _historyIndex = -1;
        _state.Settings.Save();
        _input.Clear();

        string display = string.Join(" ", args.Select(a => a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a));
        Enqueue($"{(_output.TextLength > 0 ? "\n" : "")}> adb {display}\n", Theme.Accent);

        var adb = new AdbClient(_state.AdbPath!);
        ProcessStartInfo psi = adb.CreateStartInfo(args, withSerial: false, redirectInput: true);
        var sw = Stopwatch.StartNew();
        _stopRequested = false;

        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Enqueue(e.Data + "\n", Theme.Text); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Enqueue(e.Data + "\n", Theme.Error); };
            if (!process.Start())
                throw new AdbException(Loc.T("adb.exe konnte nicht gestartet werden.", "adb.exe could not be started."));
            _process = process;
            UpdateEnabled();
            _status.Text = Loc.T("Läuft … (Stopp beendet den Befehl)", "Running … (Stop ends the command)");
            try { process.StandardInput.Close(); } catch { }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit(); // flush asynchronous output events

            string summary = _stopRequested
                ? Loc.T($"[gestoppt nach {sw.Elapsed.TotalSeconds:0.0} s]", $"[stopped after {sw.Elapsed.TotalSeconds:0.0} s]")
                : Loc.T($"[Exit-Code {process.ExitCode} · {sw.Elapsed.TotalSeconds:0.0} s]", $"[exit code {process.ExitCode} · {sw.Elapsed.TotalSeconds:0.0} s]");
            Enqueue(summary + "\n", _stopRequested || process.ExitCode != 0 ? Theme.Warning : Theme.Muted);
            process.Dispose();
        }
        catch (Exception ex)
        {
            Enqueue(ex.Message + "\n", Theme.Error);
        }
        finally
        {
            _process = null;
            _state.EndOperation();
            UpdateEnabled();
            _ = _state.RefreshDevicesAsync();
        }
    }

    private void Enqueue(string text, Color color) => _pending.Enqueue((text, color));

    private void FlushOutput()
    {
        if (_pending.IsEmpty)
            return;

        var sb = new StringBuilder();
        Color? color = null;

        void Write()
        {
            if (sb.Length == 0) return;
            if (_output.TextLength > 3_000_000)
            {
                _output.Select(0, 1_000_000);
                _output.ReadOnly = false;
                _output.SelectedText = "";
                _output.ReadOnly = true;
            }
            _output.SelectionStart = _output.TextLength;
            _output.SelectionLength = 0;
            _output.SelectionColor = color ?? Theme.Text;
            _output.AppendText(sb.ToString());
            sb.Clear();
        }

        int budget = 5000;
        while (budget-- > 0 && _pending.TryDequeue(out var item))
        {
            if (color is not null && item.Color != color)
                Write();
            color = item.Color;
            sb.Append(item.Text);
        }
        Write();
        _output.SelectionStart = _output.TextLength;
        _output.ScrollToCaret();
    }
}
