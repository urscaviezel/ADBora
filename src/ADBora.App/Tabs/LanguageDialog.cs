using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>Choose the UI language and add / remove additional languages (GitHub repository, folder "lang").</summary>
internal static class LanguageDialog
{
    private sealed class Row
    {
        public string Code = "";
        public string Name = "";
        public bool BuiltIn;
        public LanguageFile? Local;
        public OnlineLanguage? Online;
        public BrokenLanguage? Broken;   // unreadable file: shown with the reason, not selectable
        public bool Installed;           // checkbox state
        public bool Newer => Online is not null && (Local is null || Languages.ParseVersion(Online.Version) > Languages.ParseVersion(Local.Version));
    }

    /// <summary>Returns the chosen language code, or null if cancelled.</summary>
    public static async Task<string?> Show(Window? owner)
    {
        if (owner is null) return null;
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

        Window dialog = Dialogs.Dialog(Loc.T("Sprache", "Language"), 860, 560);
        var info = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Muted, Margin = new Thickness(0, 0, 0, 12),
            Text = Loc.T("Sprache wählen (Kreis). Deutsch und Englisch sind eingebaut; weitere Sprachen lassen sich aus dem GitHub-Repository herunterladen – Haken = installieren / aktualisieren, kein Haken = entfernen.",
                         "Choose the language (circle). German and English are built in; more languages can be downloaded from the GitHub repository – tick = install / update, no tick = remove.")
        };
        var status = new TextBlock { Foreground = AppTheme.Muted, Margin = new Thickness(0, 8, 0, 0),
            Text = Loc.T("Online verfügbare Sprachen werden geladen …", "Loading languages available online …") };

        var table = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*,80,190,150,90") };
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
            table.Children.Clear();
            table.RowDefinitions.Clear();
            void Cell(Control c, int row, int col, IBrush? back)
            {
                var border = new Border { Background = back, Padding = new Thickness(8, 6), Child = c, BorderBrush = AppTheme.Grid, BorderThickness = new Thickness(0, 0, 0, 1) };
                Grid.SetRow(border, row); Grid.SetColumn(border, col);
                table.Children.Add(border);
            }
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            string[] headers = { Loc.T("Aktiv", "Active"), Loc.T("Sprache", "Language"), "Version", "Status", Loc.T("Übersetzt", "Translated"), Loc.T("Installiert", "Installed") };
            for (int c = 0; c < headers.Length; c++)
                Cell(new TextBlock { Text = headers[c], FontWeight = FontWeight.SemiBold, HorizontalAlignment = c is 0 or 5 ? HorizontalAlignment.Center : HorizontalAlignment.Left }, 0, c, AppTheme.TableHeader);

            int i = 1;
            foreach (Row r in rows)
            {
                table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                IBrush back = i % 2 == 0 ? AppTheme.TableAlt : AppTheme.Card;
                string label = r.Broken is { } b ? $"{b.FileName}{(b.Online ? " (GitHub)" : "")}" : $"{r.Name} ({r.Code})";
                string check = "", tip = "";
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
                IBrush fore = r.Broken is not null ? AppTheme.Error : AppTheme.Text;

                Control activeCell = new Border();
                Control installedCell = new Border();
                if (r.Broken is null)
                {
                    Row row = r;
                    var radio = new RadioButton { GroupName = "lang", IsChecked = r.Code == active, HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 0, Padding = default };
                    var box = new CheckBox { IsChecked = r.Installed, IsEnabled = !(Locked(r) && r.Installed), HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 0, Padding = default };
                    radio.IsCheckedChanged += (_, _) =>
                    {
                        if (radio.IsChecked != true) return;
                        active = row.Code;
                        row.Installed = true;   // choosing a language installs it
                        box.IsChecked = true;
                    };
                    box.IsCheckedChanged += (_, _) =>
                    {
                        row.Installed = box.IsChecked == true;
                        if (!row.Installed && active == row.Code) { active = Loc.Language; Fill(); }
                    };
                    activeCell = radio;
                    installedCell = box;
                }
                Cell(activeCell, i, 0, back);
                Cell(new TextBlock { Text = label, Foreground = fore }, i, 1, back);
                Cell(new TextBlock { Text = r.Local?.Version ?? r.Online?.Version ?? (r.BuiltIn ? AppInfo.Version : ""), Foreground = fore }, i, 2, back);
                var st = new TextBlock { Text = StatusText(r), Foreground = fore, TextTrimming = TextTrimming.CharacterEllipsis };
                if (r.Broken is not null) ToolTip.SetTip(st, StatusText(r));
                Cell(st, i, 3, back);
                var ck = new TextBlock { Text = check, Foreground = tip.Contains('\n') || check.Contains('·') ? AppTheme.Error : fore };
                if (tip.Length > 0) ToolTip.SetTip(ck, tip);
                Cell(ck, i, 4, back);
                Cell(installedCell, i, 5, back);
                i++;
            }
        }
        Fill();

        var ok = Ui.Button("Übernehmen", "Apply", ButtonKind.Primary);
        var cancel = Ui.Button("Abbrechen", "Cancel");
        var folder = Ui.Button("Sprachordner öffnen", "Open language folder");
        bool accepted = false;
        ok.IsDefault = true;
        cancel.IsCancel = true;
        ok.Click += (_, _) => { accepted = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        folder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(Languages.UserDirectory);
                Platform.OpenFolder(Languages.UserDirectory);
            }
            catch { }
        };
        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { ok, cancel } };
        cancel.Margin = default;
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { folder } });

        var body = new DockPanel { Margin = new Thickness(22) };
        DockPanel.SetDock(info, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(status, Dock.Bottom);
        body.Children.Add(info);
        body.Children.Add(buttons);
        body.Children.Add(status);
        body.Children.Add(new Border
        {
            BorderBrush = AppTheme.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Background = AppTheme.Card,
            Child = new ScrollViewer { Content = table },
        });
        dialog.Content = body;

        // Online list in the background
        using var cts = new CancellationTokenSource();
        dialog.Opened += async (_, _) =>
        {
            try
            {
                (List<OnlineLanguage> online, List<BrokenLanguage> brokenOnline) = await Languages.OnlineWithErrorsAsync(cts.Token);
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
                status.Text = Loc.T($"Online-Liste nicht erreichbar: {ex.Message}", $"Online list not reachable: {ex.Message}");
            }
        };

        await dialog.ShowDialog(owner);
        cts.Cancel();
        if (!accepted) return null;

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
            await Dialogs.Message(owner, string.Join("\n", errors), Loc.T("Sprachen", "Languages"), MessageKind.Warning);

        Row? chosen = rows.FirstOrDefault(r => r.Code == active && r.Broken is null);
        bool available = chosen is not null && (chosen.BuiltIn || Languages.Load(chosen.Code) is not null);
        return available ? active : Loc.Language;
    }
}
