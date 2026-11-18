using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>
/// "Explore" dialog: reads apps, system properties, settings and services live
/// from the active device and builds ADB commands from them.
/// </summary>
internal static class CommandExplorer
{
    private sealed record Item(string Key, string Value, string Extra = "")
    {
        public string Display => Value.Length > 0 ? $"{Key}  =  {Value}" : Key;
    }

    private sealed record CmdAction(string De, string En, Func<Item, string> Build, CommandRisk Risk = CommandRisk.Safe);

    private sealed record Category(string De, string En, string HintDe, string HintEn, Func<AdbClient, CancellationToken, Task<List<Item>>> Load, CmdAction[] Actions);

    public sealed record Result(string Command, CommandRisk Risk, bool Save);

    private static readonly Category[] Categories =
    {
        new("Apps (nachinstalliert)", "Apps (user installed)",
            "Pakete der nachinstallierten Apps.", "Packages of the user-installed apps.",
            (adb, t) => Lines(adb, t, "pm list packages -3", l => l.StartsWith("package:") ? new Item(l[8..], "") : null),
            new CmdAction[]
            {
                new CmdAction("Starten", "Start", i => $"shell monkey -p {i.Key} -c android.intent.category.LAUNCHER 1"),
                new CmdAction("Beenden", "Stop", i => $"shell am force-stop {i.Key}"),
                new CmdAction("Version", "Version", i => $"shell dumpsys package {i.Key} | grep -E \"versionName|versionCode|firstInstallTime|lastUpdateTime\""),
                new CmdAction("Berechtigungen", "Permissions", i => $"shell dumpsys package {i.Key} | grep -E \"permission\" | head -40"),
                new CmdAction("Daten löschen", "Clear data", i => $"shell pm clear {i.Key}", CommandRisk.Dangerous),
            }),
        new("Apps (alle inkl. System)", "Apps (all incl. system)",
            "Alle Pakete inklusive System-Apps.", "All packages including system apps.",
            (adb, t) => Lines(adb, t, "pm list packages", l => l.StartsWith("package:") ? new Item(l[8..], "") : null),
            new CmdAction[]
            {
                new CmdAction("Starten", "Start", i => $"shell monkey -p {i.Key} -c android.intent.category.LAUNCHER 1"),
                new CmdAction("Beenden", "Stop", i => $"shell am force-stop {i.Key}"),
                new CmdAction("Version", "Version", i => $"shell dumpsys package {i.Key} | grep -E \"versionName|versionCode\""),
            }),
        new("Systemwerte (getprop)", "System properties (getprop)",
            "Alle Systemwerte. Setzen geht nur bei beschreibbaren Werten (z. B. debug.*), meist bis zum Neustart.",
            "All system properties. Only writable ones can be set (e.g. debug.*), usually until reboot.",
            (adb, t) => Lines(adb, t, "getprop", l =>
            {
                // [key]: [value]
                int a = l.IndexOf("]: [", StringComparison.Ordinal);
                return l.StartsWith('[') && a > 0 ? new Item(l[1..a], l[(a + 4)..].TrimEnd(']')) : null;
            }),
            new CmdAction[]
            {
                new CmdAction("Lesen", "Read", i => $"shell getprop {i.Key}"),
                new CmdAction("Setzen …", "Set …", i => $"shell setprop {i.Key} <wert>", CommandRisk.Caution),
            }),
        new("Einstellungen (settings)", "Settings (settings)",
            "Android-Einstellungen aus den Bereichen global, system und secure.", "Android settings of the namespaces global, system and secure.",
            async (adb, t) =>
            {
                var items = new List<Item>();
                foreach (string ns in new[] { "global", "system", "secure" })
                {
                    items.AddRange(await Lines(adb, t, $"settings list {ns}", l =>
                    {
                        int eq = l.IndexOf('=');
                        return eq > 0 ? new Item($"{ns} {l[..eq]}", l[(eq + 1)..], ns) : null;
                    }));
                }
                return items;
            },
            new CmdAction[]
            {
                new CmdAction("Lesen", "Read", i => $"shell settings get {i.Key}"),
                new CmdAction("Setzen …", "Put …", i => $"shell settings put {i.Key} <wert>", CommandRisk.Caution),
            }),
        new("Systemdienste (cmd)", "System services (cmd)",
            "Dienste mit Befehlsschnittstelle. „Hilfe“ zeigt die Unterbefehle des Dienstes.", "Services with a command interface. \"Help\" lists the service's sub commands.",
            (adb, t) => Lines(adb, t, "cmd -l", l => l.Length > 0 && !l.Contains(' ') ? new Item(l, "") : null),
            new CmdAction[]
            {
                new CmdAction("Hilfe", "Help", i => $"shell cmd {i.Key} help"),
                new CmdAction("Status (dumpsys)", "Status (dumpsys)", i => $"shell dumpsys {i.Key} | head -100"),
            }),
    };

    private static async Task<List<Item>> Lines(AdbClient adb, CancellationToken token, string shellLine, Func<string, Item?> parse)
    {
        AdbResult result = await adb.CaptureAsync(token, TimeSpan.FromSeconds(60), "shell", shellLine);
        var items = new List<Item>();
        foreach (string raw in result.Output.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (parse(line) is { } item) items.Add(item);
        }
        if (items.Count == 0 && !result.Ok)
            throw new AdbException(result.Combined.Trim());
        return items.OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<Result?> Show(Window owner, AppState state, AdbDevice device)
    {
        var adb = new AdbClient(state.AdbPath!, device.Serial);
        Window dialog = Dialogs.Dialog(Loc.T($"Entdecken – {device.DisplayName}", $"Explore – {device.DisplayName}"), 920, 660);
        dialog.MinWidth = 640;
        dialog.MinHeight = 480;

        // --- top: category + search --------------------------------------
        var category = Ui.Combo(300);
        category.ItemsSource = Categories.Select(c => Loc.T(c.De, c.En)).ToList();
        var search = Ui.TextBox(260);
        Ui.Placeholder(search, "Filter …", "Filter …");
        var reload = Ui.Button("Neu laden", "Reload");
        var count = new TextBlock { Foreground = AppTheme.Muted, Margin = new Thickness(8, 0, 0, 0) };
        var top = Ui.Row(category, search, reload, count);
        var hint = new TextBlock { Foreground = AppTheme.Muted, Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap };

        // --- list ---------------------------------------------------------
        var list = new ListBox
        {
            Background = AppTheme.Console,
            BorderBrush = AppTheme.CardBorder,
            BorderThickness = new Thickness(1),
            FontFamily = AppTheme.MonoFont,
            FontSize = 12.5,
            ItemTemplate = new FuncDataTemplate<Item>((item, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = item?.Key ?? "", Foreground = AppTheme.Text },
                    new TextBlock { Text = item is { Value.Length: > 0 } ? "   = " + item.Value : "", Foreground = AppTheme.Accent, TextTrimming = TextTrimming.CharacterEllipsis },
                }
            }),
        };
        list.Classes.Add("console");

        // --- bottom: actions + command ------------------------------------
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var commandLabel = Ui.Label("Befehl (bearbeitbar; Platzhalter wie <wert> ersetzen):", "Command (editable; replace placeholders like <wert>):", muted: true);
        var command = new TextBox { FontFamily = AppTheme.MonoFont };
        var take = Ui.Button("In Eingabe übernehmen", "Use in input", ButtonKind.Primary);
        var save = Ui.Button("Als eigenen Befehl speichern …", "Save as own command …");
        var close = Ui.Button("Schließen", "Close");
        close.Margin = default;
        close.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { save, take, close } };
        var bottom = new StackPanel { Children = { actions, commandLabel, command, buttons } };

        var body = new DockPanel { Margin = new Thickness(22) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(hint);
        body.Children.Add(bottom);
        body.Children.Add(list);
        dialog.Content = body;

        List<Item> all = new();
        Category current = Categories[0];
        CommandRisk risk = CommandRisk.Safe;
        CmdAction? lastAction = null;
        CancellationTokenSource? cts = null;
        Result? result = null;

        void Filter()
        {
            string f = (search.Text ?? "").Trim();
            var shown = all.Where(i => f.Length == 0 || i.Display.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            list.ItemsSource = shown;
            count.Text = Loc.T($"{shown.Count} von {all.Count}", $"{shown.Count} of {all.Count}");
        }

        void BuildActions()
        {
            actions.Children.Clear();
            foreach (CmdAction a in current.Actions)
            {
                var b = Ui.Button(a.De, a.En, a.Risk == CommandRisk.Dangerous ? ButtonKind.Danger : ButtonKind.Normal);
                b.Click += (_, _) => Apply(a);
                actions.Children.Add(b);
            }
        }

        void Apply(CmdAction a)
        {
            if (list.SelectedItem is not Item item) return;
            lastAction = a;
            command.Text = a.Build(item);
            risk = a.Risk;
            int ph = command.Text.IndexOf('<');
            if (ph >= 0 && command.Text.IndexOf('>', ph) is int end and > 0)
            {
                command.Focus();
                command.SelectionStart = ph;
                command.SelectionEnd = end + 1;
            }
        }

        async Task LoadAsync()
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            all = new List<Item>();
            list.ItemsSource = null;
            count.Text = Loc.T("Lade …", "Loading …");
            reload.IsEnabled = false;
            try
            {
                all = await current.Load(adb, token);
                if (token.IsCancellationRequested) return;
                Filter();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                count.Text = Loc.T("Fehler: ", "Error: ") + ex.Message;
            }
            finally
            {
                reload.IsEnabled = true;
            }
        }

        category.SelectionChanged += async (_, _) =>
        {
            if (category.SelectedIndex < 0) return;
            current = Categories[category.SelectedIndex];
            hint.Text = Loc.T(current.HintDe, current.HintEn);
            BuildActions();
            command.Text = "";
            await LoadAsync();
        };
        search.TextChanged += (_, _) => Filter();
        reload.Click += async (_, _) => await LoadAsync();
        list.DoubleTapped += (_, _) => { if (current.Actions.Length > 0) Apply(current.Actions[0]); };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is null || current.Actions.Length == 0) return;
            Apply(lastAction is not null && current.Actions.Contains(lastAction) ? lastAction : current.Actions[0]);
            list.Focus(); // keep arrow-key navigation in the list
        };
        command.TextChanged += (_, _) => take.IsEnabled = save.IsEnabled = (command.Text ?? "").Trim().Length > 0;
        take.IsEnabled = save.IsEnabled = false;
        take.Click += (_, _) => { result = new Result((command.Text ?? "").Trim(), risk, false); dialog.Close(); };
        save.Click += (_, _) => { result = new Result((command.Text ?? "").Trim(), risk, true); dialog.Close(); };
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => cts?.Cancel();
        dialog.Opened += (_, _) => category.SelectedIndex = 0;

        await dialog.ShowDialog(owner);
        return result;
    }
}
