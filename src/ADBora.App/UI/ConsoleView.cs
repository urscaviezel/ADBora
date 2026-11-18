using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AdbTool.Core;

namespace AdbTool.UI;

/// <summary>
/// Coloured, virtualised console output (replacement for the WinForms RichTextBox): one item per line,
/// thread-safe <see cref="Enqueue"/>, flushed every 80 ms; lines can be selected and copied (Ctrl+C).
/// </summary>
internal sealed class ConsoleView : Border
{
    internal sealed record Line(string Text, IBrush Color);

    private readonly ObservableCollection<Line> _lines = new();
    private readonly ConcurrentQueue<(string Text, IBrush Color)> _pending = new();
    private readonly ListBox _list;
    private string _partial = "";   // text of the last, not yet terminated line
    private IBrush _partialColor = AppTheme.Text;
    private const int MaxLines = 200_000;

    public ConsoleView()
    {
        Background = AppTheme.Console;
        BorderBrush = AppTheme.CardBorder;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        _list = new ListBox
        {
            ItemsSource = _lines,
            Background = Brushes.Transparent,
            SelectionMode = SelectionMode.Multiple,
            FontFamily = AppTheme.MonoFont,
            FontSize = 12.5,
            ItemTemplate = new FuncDataTemplate<Line>((line, _) => new TextBlock
            {
                Text = line?.Text ?? "",
                Foreground = line?.Color ?? AppTheme.Text,
                TextWrapping = TextWrapping.NoWrap,
            }),
        };
        _list.Classes.Add("console");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; await CopyAsync(selectedOnly: true); }
            else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; _list.SelectAll(); }
        };
        Child = _list;
        DispatcherTimer.Run(() => { Flush(); return true; }, TimeSpan.FromMilliseconds(80));
    }

    public bool IsEmpty => _lines.Count == 0 && _partial.Length == 0 && _pending.IsEmpty;

    /// <summary>Adds text (may contain several lines); any thread.</summary>
    public void Enqueue(string text, IBrush color) => _pending.Enqueue((text, color));

    public void Clear()
    {
        while (_pending.TryDequeue(out _)) { }
        _lines.Clear();
        _partial = "";
    }

    public string AllText()
    {
        Flush();
        var sb = new StringBuilder();
        foreach (Line l in _lines) sb.Append(l.Text).Append('\n');
        sb.Append(_partial);
        return sb.ToString();
    }

    public async Task CopyAsync(bool selectedOnly = false)
    {
        string text = selectedOnly && _list.SelectedItems is { Count: > 0 } sel
            ? string.Join("\n", _lines.Where(l => sel.Contains(l)).Select(l => l.Text))
            : AllText();
        if (text.Length > 0) await Dialogs.CopyText(this, text);
    }

    private void Flush()
    {
        if (_pending.IsEmpty) return;
        int budget = 5000;
        var add = new List<Line>();
        while (budget-- > 0 && _pending.TryDequeue(out var item))
        {
            string text = Privacy.MaskPaths(item.Text).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] parts = text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i == 0) { _partial += parts[0]; _partialColor = item.Color; }
                else
                {
                    add.Add(new Line(_partial, _partialColor));
                    _partial = parts[i];
                    _partialColor = item.Color;
                }
            }
        }
        foreach (Line l in add) _lines.Add(l);
        if (_lines.Count > MaxLines)
            for (int i = 0; i < MaxLines / 4; i++) _lines.RemoveAt(0);
        if (_lines.Count > 0) _list.ScrollIntoView(_lines.Count - 1);
    }
}
