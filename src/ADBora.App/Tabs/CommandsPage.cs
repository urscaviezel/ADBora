using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>Tab 7: free ADB command input with device commands, saved commands and history.</summary>
internal sealed class CommandsPage : DockPanel, IPage
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
    private readonly ComboBox _saved = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _savedLoad = Ui.Button("Übernehmen", "Load");
    private readonly Button _savedRun = Ui.Button("Ausführen", "Run");
    private readonly Button _savedAdd = Ui.Button("Aktuellen Befehl speichern …", "Save current command …");
    private readonly Button _savedDelete = Ui.Button("Löschen", "Delete");

    private readonly ComboBox _presets = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _detected = Ui.Value("");
    private readonly TextBlock _presetInfo = Ui.Value("");
    private readonly Button _presetLoad = Ui.Button("Übernehmen", "Load");
    private readonly Button _presetRun = Ui.Button("Ausführen", "Run");
    private readonly Button _presetSave = Ui.Button("Zu eigenen Befehlen …", "Add to own commands …");
    private readonly Button _explore = Ui.Button("Entdecken …", "Explore …");
    private readonly Dictionary<string, DeviceProfile> _profiles = new();
    private DeviceProfile? _profile;
    private string? _riskCommand;
    private CommandRisk _risk;

    private readonly TextBox _input = new() { FontFamily = AppTheme.MonoFont, Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox _targetDevice = Ui.CheckBox("An aktives Gerät senden (-s)", "Send to active device (-s)");
    private readonly Button _run = Ui.Button("Ausführen  ⏎", "Run  ⏎", ButtonKind.Primary);
    private readonly Button _stop = Ui.Button("Stopp", "Stop", ButtonKind.Danger);
    private readonly Button _clear = Ui.Button("Ausgabe leeren", "Clear output");
    private readonly Button _copy = Ui.Button("Ausgabe kopieren", "Copy output");
    private readonly ConsoleView _output = new();
    private readonly TextBlock _status = Ui.Value("");

    private Process? _process;
    private bool _stopRequested;
    private int _historyIndex = -1;

    public CommandsPage(AppState state)
    {
        _state = state;
        Margin = new Thickness(20, 16);

        BuildUi();
        ReloadSaved();

        _state.BusyChanged += UpdateEnabled;
        _state.SelectedDeviceChanged += UpdateEnabled;
        _state.SelectedDeviceChanged += () => _ = RefreshProfileAsync();
        _state.AdbChanged += UpdateEnabled;
        Loc.LanguageChanged += UpdateEnabled;
        Loc.LanguageChanged += () => { ReloadPresets(); ReloadSaved(); };
        ReloadPresets();
        UpdateEnabled();
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private void BuildUi()
    {
        // Saved commands ------------------------------------------------------
        var savedCard = new Card("Gespeicherte Befehle", "Saved commands");
        _saved.SelectionChanged += (_, _) => { if (_saved.IsDropDownOpen || _saved.IsKeyboardFocusWithin) LoadSelected(); UpdateEnabled(); };
        _savedLoad.Click += (_, _) => LoadSelected();
        _savedRun.Click += async (_, _) => { LoadSelected(); await RunAsync(); };
        _savedAdd.Click += async (_, _) => await SaveCurrent();
        _savedDelete.Click += async (_, _) => await DeleteSelected();
        savedCard.SetContent(_saved);
        savedCard.SetContent(Ui.Row(_savedLoad, _savedRun, _savedAdd, _savedDelete));

        // Device commands ---------------------------------------------------------
        var presetCard = new Card("Befehle für dieses Gerät", "Commands for this device");
        _detected.Foreground = AppTheme.Muted;
        _detected.Margin = new Thickness(0, 0, 0, 6);
        _presets.SelectionChanged += (_, _) => ShowPresetInfo();
        _presetInfo.Margin = new Thickness(0, 0, 0, 8);
        _presetInfo.MinHeight = 40;
        _presetLoad.Click += (_, _) => LoadPreset();
        _presetRun.Click += async (_, _) => { if (LoadPreset()) await RunAsync(); };
        _presetSave.Click += async (_, _) => { if (_presets.SelectedItem is PresetItem p) await SaveCommand(p.Preset.Command, p.Preset.Name); };
        _explore.Click += async (_, _) => await ExploreAsync();
        presetCard.SetContent(_detected);
        presetCard.SetContent(_presets);
        presetCard.SetContent(_presetInfo);
        presetCard.SetContent(Ui.Row(_presetLoad, _presetRun, _presetSave, _explore));

        var topRow = Responsive.TwoColumns(presetCard, savedCard, breakpoint: 1000, padding: default);

        // Input -----------------------------------------------------------------
        var inputCard = new Card("Befehl", "Command");
        var prefix = new TextBlock { Text = "adb", FontFamily = AppTheme.MonoFont, Foreground = AppTheme.Accent, Margin = new Thickness(0, 0, 8, 0) };
        Ui.Placeholder(_input, "z. B. shell getprop ro.product.model   ·   ↑/↓ = Verlauf", "e.g. shell getprop ro.product.model   ·   ↑/↓ = history");
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var inputRow = new DockPanel();
        DockPanel.SetDock(prefix, Dock.Left);
        inputRow.Children.Add(prefix);
        inputRow.Children.Add(_input);
        inputCard.SetContent(inputRow);

        _targetDevice.IsChecked = _state.Settings.CommandTargetSelectedDevice;
        _targetDevice.IsCheckedChanged += (_, _) =>
        {
            _state.Settings.CommandTargetSelectedDevice = _targetDevice.IsChecked == true;
            _state.Settings.Save();
        };
        _run.Click += async (_, _) => await RunAsync();
        _stop.Click += (_, _) => CancelOperation();
        _clear.Click += (_, _) => _output.Clear();
        _copy.Click += async (_, _) => await _output.CopyAsync();
        var actions = Ui.Row(_run, _stop, _clear, _copy);
        actions.Margin = new Thickness(0, 8, 0, 0);
        inputCard.SetContent(actions);
        _targetDevice.Margin = new Thickness(0, 8, 0, 0);
        inputCard.SetContent(_targetDevice);
        var hint = Ui.Label(
            "Das Präfix „adb“ ist optional. Bei „shell …“ wird der Rest der Zeile unverändert an die Geräte-Shell übergeben (Pipes, Anführungszeichen). " +
            "Interaktive Befehle ohne Argumente (z. B. nur „shell“) werden nicht unterstützt; Dauerläufer wie „logcat“ mit Stopp beenden.",
            "The \"adb\" prefix is optional. With \"shell …\" the rest of the line is passed unchanged to the device shell (pipes, quotes). " +
            "Interactive commands without arguments (e.g. just \"shell\") are not supported; stop long-running commands such as \"logcat\" with Stop.",
            muted: true);
        hint.Margin = new Thickness(0, 8, 0, 0);
        inputCard.SetContent(hint);

        // Output ----------------------------------------------------------------
        var outputTitle = Ui.Label("Ausgabe", "Output");
        outputTitle.Classes.Add("section");
        outputTitle.Margin = new Thickness(0, 0, 0, 8);
        _status.Foreground = AppTheme.Muted;
        _status.Margin = new Thickness(0, 8, 0, 0);
        var outputBody = new DockPanel();
        DockPanel.SetDock(outputTitle, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        outputBody.Children.Add(outputTitle);
        outputBody.Children.Add(_status);
        outputBody.Children.Add(_output);
        var outputCard = new Border
        {
            Background = AppTheme.Card, BorderBrush = AppTheme.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 14), MinHeight = 220, Child = outputBody,
        };

        topRow.Margin = new Thickness(0, 0, 0, 0);
        SetDock(topRow, Dock.Top);
        SetDock(inputCard, Dock.Top);
        Children.Add(topRow);
        Children.Add(inputCard);
        Children.Add(outputCard);
    }

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
        var items = CommandCatalog.For(_profile?.Family)
            .OrderBy(p => p.Family is null ? 1 : 0)
            .ThenBy(p => p.Category, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PresetItem(p)).ToList();
        _presets.ItemsSource = items;
        int index = previous is null ? 0 : Math.Max(0, items.FindIndex(i => i.Preset.Command == previous));
        if (items.Count > 0) _presets.SelectedIndex = index;

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
        _presetInfo.Foreground = p.Risk == CommandRisk.Dangerous ? AppTheme.Error : p.Risk == CommandRisk.Caution ? AppTheme.Warning : AppTheme.Muted;
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
        {
            _input.SelectionStart = ph;
            _input.SelectionEnd = end + 1;
        }
        else
            _input.CaretIndex = command.Length;
    }

    private async Task ExploreAsync()
    {
        if (_state.SelectedDevice is not { IsReady: true } device || !_state.HasAdb || Owner is not { } owner) return;
        CommandExplorer.Result? result = await CommandExplorer.Show(owner, _state, device);
        if (result is null) return;
        if (result.Save)
            await SaveCommand(result.Command, "");
        else
            SetInput(result.Command, result.Risk);
    }

    private void ReloadSaved(string? select = null)
    {
        string model = CurrentModel;
        var items = _state.Settings.SavedCommands
            .Where(c => c.Model.Length == 0 || string.Equals(c.Model, model, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new SavedItem(c)).ToList();
        _saved.ItemsSource = items;
        if (items.Count > 0)
            _saved.SelectedIndex = select is null ? 0 : Math.Max(0, items.FindIndex(i => i.Command.Name == select));
        UpdateEnabled();
    }

    private void LoadSelected()
    {
        if (_saved.SelectedItem is SavedItem item)
            SetInput(item.Command.Command, CommandRisk.Safe);
    }

    private async Task SaveCurrent()
    {
        string command = StripAdbPrefix((_input.Text ?? "").Trim());
        if (command.Length == 0)
        {
            await Dialogs.Message(Owner, Loc.T("Bitte zuerst einen Befehl eingeben.", "Please enter a command first."));
            return;
        }
        string suggestion = _saved.SelectedItem is SavedItem item && item.Command.Command == command ? item.Command.Name : "";
        await SaveCommand(command, suggestion);
    }

    private async Task SaveCommand(string command, string suggestion)
    {
        command = StripAdbPrefix(command.Trim());
        if (command.Length == 0 || Owner is not { } owner) return;
        string model = CurrentModel;
        var input = await InputDialog.Show(owner, Loc.T("Befehl speichern", "Save command"), Loc.T("Name für diesen Befehl:", "Name for this command:"), suggestion,
            model.Length > 0 ? Loc.T($"Nur für dieses Gerätemodell anzeigen ({model})", $"Show only for this device model ({model})") : null);
        if (input is not { } r || string.IsNullOrWhiteSpace(r.Text))
            return;
        string name = r.Text.Trim();
        bool onlyModel = model.Length > 0 && r.Checked;

        SavedCommand? existing = _state.Settings.SavedCommands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null)
        {
            if (!await Dialogs.Message(owner, Loc.T($"„{name}“ existiert bereits. Überschreiben?", $"\"{name}\" already exists. Overwrite?"), "ADBora", MessageKind.Question))
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

    private async Task DeleteSelected()
    {
        if (_saved.SelectedItem is not SavedItem item)
            return;
        if (!await Dialogs.Message(Owner, Loc.T($"Gespeicherten Befehl „{item.Command.Name}“ löschen?", $"Delete saved command \"{item.Command.Name}\"?"), "ADBora", MessageKind.Question))
            return;
        _state.Settings.SavedCommands.Remove(item.Command);
        _state.Settings.Save();
        ReloadSaved();
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
        _run.IsEnabled = idle && _state.HasAdb;
        _savedRun.IsEnabled = idle && _state.HasAdb && _saved.SelectedItem is not null;
        _savedLoad.IsEnabled = _savedDelete.IsEnabled = _saved.SelectedItem is not null;
        _stop.IsEnabled = running;
        _targetDevice.IsEnabled = !running;
        bool ready = _state.HasAdb && _state.SelectedDevice is { IsReady: true };
        bool hasPreset = _presets.SelectedItem is PresetItem;
        _presetLoad.IsEnabled = _presetSave.IsEnabled = hasPreset;
        _presetRun.IsEnabled = hasPreset && idle && ready && !((PresetItem)_presets.SelectedItem!).Preset.NeedsInput;
        _explore.IsEnabled = idle && ready;

        if (!running)
        {
            _status.Text = !_state.HasAdb
                ? Loc.T("ADB ist nicht eingerichtet (Tab „Allgemeine Einstellungen“).", "ADB is not set up (\"General settings\" tab).")
                : _state.IsBusy
                    ? Loc.T("Ein anderer ADB-Vorgang läuft gerade.", "Another ADB operation is running.")
                    : _state.SelectedDevice is { } d
                        ? Loc.T($"Aktives Gerät: {d.DisplayName} ({d.ShownSerial})", $"Active device: {d.DisplayName} ({d.ShownSerial})")
                        : Loc.T("Kein Gerät ausgewählt – nur Host-Befehle (z. B. devices, version) sinnvoll.", "No device selected – only host commands (e.g. devices, version) make sense.");
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        List<string> history = _state.Settings.CommandHistory;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_run.IsEnabled) _ = RunAsync();
        }
        else if (e.Key == Key.Up && history.Count > 0)
        {
            e.Handled = true;
            _historyIndex = _historyIndex < 0 ? history.Count - 1 : Math.Max(0, _historyIndex - 1);
            _input.Text = history[_historyIndex];
            _input.CaretIndex = _input.Text.Length;
        }
        else if (e.Key == Key.Down && history.Count > 0)
        {
            e.Handled = true;
            if (_historyIndex < 0) return;
            _historyIndex++;
            if (_historyIndex >= history.Count)
            {
                _historyIndex = -1;
                _input.Text = "";
            }
            else
            {
                _input.Text = history[_historyIndex];
                _input.CaretIndex = _input.Text.Length;
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
        if (!hasTarget && _targetDevice.IsChecked == true && _state.SelectedDevice is { } device && !HostCommands.Contains(command))
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
        string line = StripAdbPrefix((_input.Text ?? "").Trim());
        if (line.Length == 0 || _process is not null || !_state.HasAdb)
            return;

        if (line.Contains('<') && line.Contains('>') && System.Text.RegularExpressions.Regex.IsMatch(line, @"<[^<>\s|]+>"))
        {
            _output.Enqueue(Loc.T("Bitte zuerst den Platzhalter <…> im Befehl ersetzen.\n", "Please replace the placeholder <…> in the command first.\n"), AppTheme.Warning);
            return;
        }

        if (_riskCommand is not null && line == StripAdbPrefix(_riskCommand) && _risk != CommandRisk.Safe)
        {
            string question = _risk == CommandRisk.Dangerous
                ? Loc.T($"Dieser Befehl kann Daten löschen oder das Gerät in einen besonderen Modus versetzen:\n\nadb {line}\n\nWirklich ausführen?",
                        $"This command can delete data or put the device into a special mode:\n\nadb {line}\n\nReally run it?")
                : Loc.T($"Dieser Befehl ändert das Verhalten des Geräts:\n\nadb {line}\n\nAusführen?",
                        $"This command changes the device behaviour:\n\nadb {line}\n\nRun it?");
            if (!await Dialogs.Message(Owner, question, "ADBora", MessageKind.Question, defaultNo: true))
                return;
        }

        List<string>? args = BuildArguments(line, out string? error);
        if (args is null)
        {
            if (error is not null) _output.Enqueue(error + "\n", AppTheme.Warning);
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
        _input.Text = "";

        string display = string.Join(" ", args.Select(a => a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a));
        _output.Enqueue($"{(_output.IsEmpty ? "" : "\n")}> adb {display}\n", AppTheme.Accent);

        var adb = new AdbClient(_state.AdbPath!);
        ProcessStartInfo psi = adb.CreateStartInfo(args, withSerial: false, redirectInput: true);
        var sw = Stopwatch.StartNew();
        _stopRequested = false;

        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) _output.Enqueue(e.Data + "\n", AppTheme.Text); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _output.Enqueue(e.Data + "\n", AppTheme.Error); };
            if (!process.Start())
                throw new AdbException(Loc.T("ADB konnte nicht gestartet werden.", "ADB could not be started."));
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
            _output.Enqueue(summary + "\n", _stopRequested || process.ExitCode != 0 ? AppTheme.Warning : AppTheme.Muted);
            process.Dispose();
        }
        catch (Exception ex)
        {
            _output.Enqueue(ex.Message + "\n", AppTheme.Error);
        }
        finally
        {
            _process = null;
            _state.EndOperation();
            UpdateEnabled();
            _ = _state.RefreshDevicesAsync();
        }
    }
}
