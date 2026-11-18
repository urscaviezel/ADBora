using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using AdbTool.Core;

namespace AdbTool.UI;

internal enum MessageKind { Info, Warning, Error, Question }

/// <summary>Dark dialogs (replacement for MessageBox / OpenFileDialog of WinForms).</summary>
internal static class Dialogs
{
    /// <summary>Dark, centred dialog window (sizes in device-independent pixels).</summary>
    public static Window Dialog(string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        MinWidth = Math.Min(width, 420),
        MinHeight = Math.Min(height, 200),
        CanResize = true,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
        Icon = MainWindow.AppIcon,
    };

    /// <summary>Message with OK, or Yes/No for <see cref="MessageKind.Question"/>; true = OK/Yes.</summary>
    public static async Task<bool> Message(Window? owner, string text, string title = "ADBora", MessageKind kind = MessageKind.Info, bool defaultNo = false)
    {
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = 620,
            CanResize = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = owner is null,
            Icon = MainWindow.AppIcon,
        };
        bool result = false;
        var symbol = new TextBlock
        {
            Text = kind switch { MessageKind.Error => "✖", MessageKind.Warning => "⚠", MessageKind.Question => "?", _ => "ℹ" },
            Foreground = kind switch { MessageKind.Error => AppTheme.Error, MessageKind.Warning or MessageKind.Question => AppTheme.Warning, _ => AppTheme.Accent },
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 16, 0),
        };
        var message = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 20, 0, 0) };
        if (kind == MessageKind.Question)
        {
            var yes = Ui.Button("Ja", "Yes", defaultNo ? ButtonKind.Normal : ButtonKind.Primary);
            var no = Ui.Button("Nein", "No", defaultNo ? ButtonKind.Primary : ButtonKind.Normal);
            yes.Margin = no.Margin = default;
            yes.Click += (_, _) => { result = true; dialog.Close(); };
            no.Click += (_, _) => dialog.Close();
            (defaultNo ? no : yes).IsDefault = true;
            no.IsCancel = true;
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
        }
        else
        {
            var ok = Ui.Button("OK", "OK", ButtonKind.Primary);
            ok.Margin = default;
            ok.MinWidth = 90;
            ok.IsDefault = ok.IsCancel = true;
            ok.Click += (_, _) => { result = true; dialog.Close(); };
            buttons.Children.Add(ok);
        }
        var body = new DockPanel { Margin = new Thickness(24, 22, 24, 18) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(symbol, Dock.Left);
        body.Children.Add(buttons);
        body.Children.Add(symbol);
        body.Children.Add(message);
        dialog.Content = body;

        if (owner is not null) await dialog.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }
        return result;
    }

    /// <summary>File picker for one file; null if cancelled.</summary>
    public static async Task<string?> OpenFile(Window owner, string title, string? startFolder, params (string Name, string[] Patterns)[] filters)
    {
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList(),
        };
        if (startFolder is not null && Directory.Exists(startFolder))
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Folder picker; null if cancelled.</summary>
    public static async Task<string?> OpenFolder(Window owner, string title, string? startFolder = null)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (startFolder is not null && Directory.Exists(startFolder))
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public static async Task CopyText(Visual visual, string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(visual)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        }
        catch { }
    }
}

internal static class InputDialog
{
    /// <summary>Single-line text input with an optional check box; null if cancelled.</summary>
    public static async Task<(string Text, bool Checked)?> Show(Window owner, string title, string label, string value, string? checkText = null)
    {
        Window dialog = Dialogs.Dialog(title, 520, checkText is null ? 190 : 230);
        dialog.CanResize = false;
        var box = new TextBox { Text = value, Margin = new Thickness(0, 8, 0, 8) };
        var check = new CheckBox { Content = checkText, IsVisible = checkText is not null };
        var ok = Ui.Button("Speichern", "Save", ButtonKind.Primary);
        var cancel = Ui.Button("Abbrechen", "Cancel");
        cancel.Margin = default;
        ok.IsDefault = true;
        cancel.IsCancel = true;
        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } };
        var body = new StackPanel { Margin = new Thickness(20), Children = { new TextBlock { Text = label }, box, check, buttons } };
        dialog.Content = body;
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await dialog.ShowDialog(owner);
        return accepted ? (box.Text ?? "", check.IsChecked == true) : null;
    }
}
