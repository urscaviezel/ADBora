using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AdbTool.Backup;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool.Tabs;

/// <summary>Selection dialog: which apps and parts (APK / OBB / Data) to restore.</summary>
internal static class RestoreDialog
{
    public static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB"
        : bytes >= 1024L * 1024 || bytes == 0
            ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
            : $"{Math.Max(1, bytes / 1024.0):0} KB";

    private const string Columns = "*,64,64,64,96";

    public static async Task<List<RestoreEntry>?> Show(Window owner, string path, List<RestoreEntry> entries, string deviceName)
    {
        Window dialog = Dialogs.Dialog(Loc.T("Batch-Restore", "Batch restore"), 900, 620);
        dialog.MinWidth = 620;
        dialog.MinHeight = 420;

        var title = new TextBlock { Text = Loc.T($"Wiederherstellen auf {deviceName}", $"Restore to {deviceName}"), Margin = new Thickness(0, 0, 0, 4) };
        title.Classes.Add("section");
        var source = new TextBlock
        {
            Foreground = AppTheme.Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 10),
            Text = Loc.T($"{entries.Count} App(s) in: {Privacy.MaskPaths(path)}", $"{entries.Count} app(s) in: {Privacy.MaskPaths(path)}")
        };
        var summary = new TextBlock { Margin = new Thickness(0, 8, 0, 4) };
        var note = new TextBlock
        {
            Foreground = AppTheme.Muted, TextWrapping = TextWrapping.Wrap,
            Text = Loc.T("Reihenfolge je App: APK installieren → OBB kopieren → Daten kopieren (die App wird dafür beendet). Vorhandene Dateien werden überschrieben. Klick auf eine Spaltenüberschrift wählt die ganze Spalte.",
                         "Order per app: install APK → copy OBB → copy data (the app is stopped first). Existing files are overwritten. Click a column header to toggle the whole column.")
        };

        var restore = Ui.Button("Wiederherstellen", "Restore", ButtonKind.Primary);
        var cancel = Ui.Button("Abbrechen", "Cancel");
        var all = Ui.Button("Alles auswählen", "Select all");
        var none = Ui.Button("Nichts auswählen", "Select none");
        cancel.Margin = default;
        restore.IsDefault = true;
        cancel.IsCancel = true;
        bool accepted = false;
        restore.Click += (_, _) => { accepted = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { restore, cancel } };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { all, none } });

        // Table: header + rows (rebuilt on every change – a few hundred apps at most)
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Background = AppTheme.TableHeader, MinHeight = 36 };
        var rows = new StackPanel();
        void UpdateSummary()
        {
            var selected = entries.Where(e => e.AnySelected).ToList();
            summary.Text = Loc.T(
                $"{selected.Count} von {entries.Count} Apps ausgewählt · APK {selected.Count(e => e.Apk)} · OBB {selected.Count(e => e.Obb)} · Daten {selected.Count(e => e.Data)} · {FormatSize(selected.Sum(e => e.SelectedBytes))}",
                $"{selected.Count} of {entries.Count} apps selected · APK {selected.Count(e => e.Apk)} · OBB {selected.Count(e => e.Obb)} · Data {selected.Count(e => e.Data)} · {FormatSize(selected.Sum(e => e.SelectedBytes))}");
            restore.IsEnabled = selected.Count > 0;
            Fill();
        }

        void SetColumn(int column)
        {
            bool Has(RestoreEntry e) => column switch { 1 => e.HasApk, 2 => e.HasObb, _ => e.HasData };
            bool Get(RestoreEntry e) => column switch { 1 => e.Apk, 2 => e.Obb, _ => e.Data };
            bool value = !entries.Where(Has).All(Get);
            foreach (RestoreEntry e in entries.Where(Has))
            {
                if (column == 1) e.Apk = value;
                else if (column == 2) e.Obb = value;
                else e.Data = value;
            }
            UpdateSummary();
        }

        void H(int col, string text, HorizontalAlignment align, bool clickable)
        {
            var t = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, HorizontalAlignment = align, Margin = new Thickness(8, 0) };
            Control cell = t;
            if (clickable)
            {
                var b = new Border { Background = Brushes.Transparent, Child = t, Cursor = new Cursor(StandardCursorType.Hand) };
                b.PointerPressed += (_, _) => SetColumn(col);
                cell = b;
            }
            Grid.SetColumn(cell, col);
            header.Children.Add(cell);
        }
        H(0, Loc.T("App / Paket", "App / package"), HorizontalAlignment.Left, false);
        H(1, "APK", HorizontalAlignment.Center, true);
        H(2, "OBB", HorizontalAlignment.Center, true);
        H(3, Loc.T("Daten", "Data"), HorizontalAlignment.Center, true);
        H(4, Loc.T("Größe", "Size"), HorizontalAlignment.Right, false);

        void Fill()
        {
            rows.Children.Clear();
            int i = 0;
            foreach (RestoreEntry entry in entries)
            {
                RestoreEntry e = entry;
                var grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions(Columns), MinHeight = 48,
                    Background = i++ % 2 == 1 ? AppTheme.TableAlt : AppTheme.Card,
                };
                IBrush dim = e.AnySelected ? AppTheme.Text : AppTheme.DisabledText;
                var names = new Border
                {
                    Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new StackPanel
                    {
                        Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = e.Name + (e.Version.Length > 0 ? "  ·  " + e.Version : ""), FontWeight = FontWeight.SemiBold, Foreground = dim, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = e.Package + (e.Run.Length > 0 ? "  ·  " + e.Run : ""), Foreground = AppTheme.Muted, TextTrimming = TextTrimming.CharacterEllipsis },
                        }
                    }
                };
                // click on the app = all parts on / off
                names.PointerPressed += (_, _) =>
                {
                    bool any = e.AnySelected;
                    e.Apk = !any && e.HasApk;
                    e.Obb = !any && e.HasObb;
                    e.Data = !any && e.HasData;
                    UpdateSummary();
                };
                grid.Children.Add(names);

                void Box(int col, bool has, Func<bool> get, Action<bool> set)
                {
                    Control c = has
                        ? new CheckBox { IsChecked = get(), HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 0, Padding = default }
                        : new TextBlock { Text = "–", Foreground = AppTheme.DisabledText, HorizontalAlignment = HorizontalAlignment.Center };
                    if (c is CheckBox box) box.IsCheckedChanged += (_, _) => { set(box.IsChecked == true); UpdateSummary(); };
                    Grid.SetColumn(c, col);
                    grid.Children.Add(c);
                }
                Box(1, e.HasApk, () => e.Apk, v => e.Apk = v);
                Box(2, e.HasObb, () => e.Obb, v => e.Obb = v);
                Box(3, e.HasData, () => e.Data, v => e.Data = v);
                var size = new TextBlock { Text = FormatSize(e.SelectedBytes), Foreground = dim, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
                Grid.SetColumn(size, 4);
                grid.Children.Add(size);
                rows.Children.Add(new Border { BorderBrush = AppTheme.Grid, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid });
            }
        }

        all.Click += (_, _) =>
        {
            foreach (RestoreEntry e in entries) { e.Apk = e.HasApk; e.Obb = e.HasObb; e.Data = e.HasData; }
            UpdateSummary();
        };
        none.Click += (_, _) =>
        {
            foreach (RestoreEntry e in entries) { e.Apk = e.Obb = e.Data = false; }
            UpdateSummary();
        };

        var table = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        table.Children.Add(new ScrollViewer { Content = rows });

        var body = new DockPanel { Margin = new Thickness(22) };
        foreach (Control c in new Control[] { title, source }) { DockPanel.SetDock(c, Dock.Top); body.Children.Add(c); }
        foreach (Control c in new Control[] { buttons, summary, note }) { DockPanel.SetDock(c, Dock.Bottom); body.Children.Add(c); }
        body.Children.Add(new Border { BorderBrush = AppTheme.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = AppTheme.Card, Child = table });
        dialog.Content = body;

        UpdateSummary();
        await dialog.ShowDialog(owner);
        return accepted ? entries.Where(e => e.AnySelected).ToList() : null;
    }
}
