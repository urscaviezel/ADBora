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

    private sealed record Action(string De, string En, Func<Item, string> Build, CommandRisk Risk = CommandRisk.Safe);

    private sealed record Category(string De, string En, string HintDe, string HintEn, Func<AdbClient, CancellationToken, Task<List<Item>>> Load, Action[] Actions);

    public sealed record Result(string Command, CommandRisk Risk, bool Save);

    private static readonly Category[] Categories =
    {
        new("Apps (nachinstalliert)", "Apps (user installed)",
            "Pakete der nachinstallierten Apps.", "Packages of the user-installed apps.",
            (adb, t) => Lines(adb, t, "pm list packages -3", l => l.StartsWith("package:") ? new Item(l[8..], "") : null),
            new[]
            {
                new Action("Starten", "Start", i => $"shell monkey -p {i.Key} -c android.intent.category.LAUNCHER 1"),
                new Action("Beenden", "Stop", i => $"shell am force-stop {i.Key}"),
                new Action("Version", "Version", i => $"shell dumpsys package {i.Key} | grep -E \"versionName|versionCode|firstInstallTime|lastUpdateTime\""),
                new Action("Berechtigungen", "Permissions", i => $"shell dumpsys package {i.Key} | grep -E \"permission\" | head -40"),
                new Action("Daten löschen", "Clear data", i => $"shell pm clear {i.Key}", CommandRisk.Dangerous),
            }),
        new("Apps (alle inkl. System)", "Apps (all incl. system)",
            "Alle Pakete inklusive System-Apps.", "All packages including system apps.",
            (adb, t) => Lines(adb, t, "pm list packages", l => l.StartsWith("package:") ? new Item(l[8..], "") : null),
            new[]
            {
                new Action("Starten", "Start", i => $"shell monkey -p {i.Key} -c android.intent.category.LAUNCHER 1"),
                new Action("Beenden", "Stop", i => $"shell am force-stop {i.Key}"),
                new Action("Version", "Version", i => $"shell dumpsys package {i.Key} | grep -E \"versionName|versionCode\""),
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
            new[]
            {
                new Action("Lesen", "Read", i => $"shell getprop {i.Key}"),
                new Action("Setzen …", "Set …", i => $"shell setprop {i.Key} <wert>", CommandRisk.Caution),
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
            new[]
            {
                new Action("Lesen", "Read", i => $"shell settings get {i.Key}"),
                new Action("Setzen …", "Put …", i => $"shell settings put {i.Key} <wert>", CommandRisk.Caution),
            }),
        new("Systemdienste (cmd)", "System services (cmd)",
            "Dienste mit Befehlsschnittstelle. „Hilfe“ zeigt die Unterbefehle des Dienstes.", "Services with a command interface. \"Help\" lists the service's sub commands.",
            (adb, t) => Lines(adb, t, "cmd -l", l => l.Length > 0 && !l.Contains(' ') ? new Item(l, "") : null),
            new[]
            {
                new Action("Hilfe", "Help", i => $"shell cmd {i.Key} help"),
                new Action("Status (dumpsys)", "Status (dumpsys)", i => $"shell dumpsys {i.Key} | head -100"),
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

    public static Result? Show(IWin32Window? owner, AppState state, AdbDevice device)
    {
        var adb = new AdbClient(state.AdbPath!, device.Serial);
        using Form dialog = Ui.Dialog(Loc.T($"Entdecken – {device.DisplayName}", $"Explore – {device.DisplayName}"), 920, 660);
        dialog.MinimumSize = new Size(Theme.S(640), Theme.S(480));

        // --- top: category + search --------------------------------------
        var category = Ui.Combo(Theme.S(300));
        foreach (Category c in Categories) category.Items.Add(Loc.T(c.De, c.En));
        var search = Ui.TextBox(Theme.S(260));
        Ui.Placeholder(search, "Filter …", "Filter …");
        var reload = Ui.Button("Neu laden", "Reload");
        var count = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(Theme.S(8), Theme.S(10), 0, 0) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Color.Transparent };
        top.Controls.AddRange(new Control[] { category, search, reload, count });
        var hint = new Label { Dock = DockStyle.Top, AutoSize = false, Height = Theme.S(28), ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };

        // --- list ---------------------------------------------------------
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Console,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.MonoFont,
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Theme.S(22),
            HorizontalScrollbar = true
        };
        Theme.UseDarkScrollBars(list);
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var back = new SolidBrush(selected ? Theme.Selection : Theme.Console);
            e.Graphics.FillRectangle(back, e.Bounds);
            if (list.Items[e.Index] is Item item)
            {
                var r = new Rectangle(e.Bounds.X + Theme.S(6), e.Bounds.Y, e.Bounds.Width - Theme.S(8), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, item.Key, Theme.MonoFont, r, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                if (item.Value.Length > 0)
                {
                    int w = TextRenderer.MeasureText(e.Graphics, item.Key, Theme.MonoFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                    var vr = new Rectangle(r.X + w + Theme.S(14), r.Y, Math.Max(10, r.Width - w - Theme.S(14)), r.Height);
                    TextRenderer.DrawText(e.Graphics, "= " + item.Value, Theme.MonoFont, vr, Theme.Accent,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                }
            }
        };

        // --- bottom: actions + command ------------------------------------
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Padding = new Padding(0, Theme.S(8), 0, 0) };
        var commandLabel = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = Theme.S(26), ForeColor = Theme.Muted, TextAlign = ContentAlignment.BottomLeft };
        Loc.Bind(commandLabel, "Befehl (bearbeitbar; Platzhalter wie <wert> ersetzen):", "Command (editable; replace placeholders like <wert>):");
        var command = Ui.TextBox(100);
        command.Dock = DockStyle.Bottom;
        command.Font = Theme.MonoFont;
        var take = Ui.Button("In Eingabe übernehmen", "Use in input", ButtonKind.Primary);
        var save = Ui.Button("Als eigenen Befehl speichern …", "Save as own command …");
        var close = Ui.Button("Schließen", "Close");
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.Transparent, Padding = new Padding(0, Theme.S(10), 0, 0) };
        close.Margin = new Padding(0);
        buttons.Controls.Add(close);
        buttons.Controls.Add(take);
        buttons.Controls.Add(save);

        dialog.Controls.Add(list);
        dialog.Controls.Add(hint);
        dialog.Controls.Add(top);
        dialog.Controls.Add(actions);
        dialog.Controls.Add(commandLabel);
        dialog.Controls.Add(command);
        dialog.Controls.Add(buttons);
        dialog.CancelButton = close;

        List<Item> all = new();
        Category current = Categories[0];
        CommandRisk risk = CommandRisk.Safe;
        Action? lastAction = null;
        CancellationTokenSource? cts = null;
        Result? result = null;

        void Filter()
        {
            string f = search.Text.Trim();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Item i in all.Where(i => f.Length == 0 || i.Display.Contains(f, StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(i);
            list.EndUpdate();
            count.Text = Loc.T($"{list.Items.Count} von {all.Count}", $"{list.Items.Count} of {all.Count}");
        }

        void BuildActions()
        {
            actions.Controls.Clear();
            foreach (Action a in current.Actions)
            {
                var b = Ui.Button(a.De, a.En, a.Risk == CommandRisk.Dangerous ? ButtonKind.Danger : ButtonKind.Normal);
                b.Padding = new Padding(Theme.S(12), Theme.S(6), Theme.S(12), Theme.S(6));
                b.Margin = new Padding(0, 0, Theme.S(8), 0);
                b.Click += (_, _) => Apply(a);
                actions.Controls.Add(b);
            }
        }

        void Apply(Action a)
        {
            if (list.SelectedItem is not Item item) return;
            lastAction = a;
            command.Text = a.Build(item);
            risk = a.Risk;
            int ph = command.Text.IndexOf('<');
            if (ph >= 0 && command.Text.IndexOf('>', ph) is int end and > 0)
            {
                command.Focus();
                command.Select(ph, end - ph + 1);
            }
        }

        async Task LoadAsync()
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            all = new List<Item>();
            list.Items.Clear();
            count.Text = Loc.T("Lade …", "Loading …");
            reload.Enabled = false;
            try
            {
                all = await current.Load(adb, token);
                if (token.IsCancellationRequested) return;
                Filter();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!dialog.IsDisposed) count.Text = Loc.T("Fehler: ", "Error: ") + ex.Message;
            }
            finally
            {
                if (!dialog.IsDisposed) reload.Enabled = true;
            }
        }

        category.SelectionChangeCommitted += async (_, _) =>
        {
            current = Categories[Math.Max(0, category.SelectedIndex)];
            hint.Text = Loc.T(current.HintDe, current.HintEn);
            BuildActions();
            command.Clear();
            await LoadAsync();
        };
        search.TextChanged += (_, _) => Filter();
        reload.Click += async (_, _) => await LoadAsync();
        list.DoubleClick += (_, _) => { if (current.Actions.Length > 0) Apply(current.Actions[0]); };
        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedItem is null || current.Actions.Length == 0) return;
            Apply(lastAction is not null && current.Actions.Contains(lastAction) ? lastAction : current.Actions[0]);
            list.Focus(); // keep arrow-key navigation in the list
        };
        command.TextChanged += (_, _) => take.Enabled = save.Enabled = command.Text.Trim().Length > 0;
        take.Enabled = save.Enabled = false;
        take.Click += (_, _) => { result = new Result(command.Text.Trim(), risk, false); dialog.Close(); };
        save.Click += (_, _) => { result = new Result(command.Text.Trim(), risk, true); dialog.Close(); };
        close.Click += (_, _) => dialog.Close();
        dialog.FormClosed += (_, _) => cts?.Cancel();
        dialog.Shown += async (_, _) =>
        {
            category.SelectedIndex = 0;
            hint.Text = Loc.T(current.HintDe, current.HintEn);
            BuildActions();
            await LoadAsync();
        };

        Ui.ShowDialog(dialog, owner);
        return result;
    }
}
