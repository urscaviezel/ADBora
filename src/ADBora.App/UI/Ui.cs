using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AdbTool.Core;

namespace AdbTool.UI;

internal enum ButtonKind { Normal, Primary, Danger }

internal interface IPage
{
    /// <summary>Called when the page becomes visible.</summary>
    void OnActivated();

    /// <summary>Cancels a running operation of this page (if any).</summary>
    void CancelOperation();
}

/// <summary>Factory helpers so every page looks the same (same names as in the WinForms version).</summary>
internal static class Ui
{
    /// <summary>Binds a text (DE/EN pair, other languages via files) to a TextBlock, Button, CheckBox …</summary>
    public static T Bind<T>(T control, string de, string en) where T : Control
    {
        switch (control)
        {
            case TextBlock t: Loc.Bind(control, v => t.Text = v, de, en); break;
            case ContentControl c: Loc.Bind(control, v => c.Content = v, de, en); break;
            case TextBox b: Loc.Bind(control, v => b.PlaceholderText = v, de, en); break;
        }
        return control;
    }

    public static TextBlock Label(string de, string en, bool muted = false, bool bold = false)
    {
        var label = new TextBlock { Margin = new Thickness(0, 6, 12, 6), TextWrapping = TextWrapping.Wrap };
        if (muted) label.Classes.Add("muted");
        if (bold) label.Classes.Add("bold");
        return Bind(label, de, en);
    }

    public static TextBlock Value(string text = "-") =>
        new() { Text = text, Margin = new Thickness(0, 6, 0, 6), TextWrapping = TextWrapping.Wrap };

    public static Button Button(string de, string en, ButtonKind kind = ButtonKind.Normal)
    {
        var button = new Button { Margin = new Thickness(0, 0, 8, 0) };
        if (kind == ButtonKind.Primary) button.Classes.Add("primary");
        if (kind == ButtonKind.Danger) button.Classes.Add("danger");
        return Bind(button, de, en);
    }

    public static TextBox TextBox(double width = 120) => new() { Width = width, Margin = new Thickness(0, 4, 8, 4) };

    public static ComboBox Combo(double width = 200) => new() { Width = width, Margin = new Thickness(0, 4, 8, 4) };

    public static void Placeholder(TextBox box, string de, string en) => Loc.Bind(box, v => box.PlaceholderText = v, de, en);

    public static CheckBox CheckBox(string de, string en) => Bind(new CheckBox { Margin = new Thickness(0, 2, 16, 2) }, de, en);

    public static RadioButton Radio(string de, string en) => Bind(new RadioButton { Margin = new Thickness(0, 2, 20, 2) }, de, en);

    /// <summary>Horizontal row of controls (wraps to the next line when narrow).</summary>
    public static WrapPanel Row(params Control[] controls)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4), ItemSpacing = 0, LineSpacing = 6 };
        foreach (Control c in controls)
        {
            c.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(c);
        }
        return row;
    }

    /// <summary>Two-column (or n-column) label/value table: all columns auto, the last one takes the rest.</summary>
    public static FormGrid Grid(int columns) => new(columns);

    public static void AddRow(FormGrid grid, params Control[] controls) => grid.AddRow(controls);

    public static TextBox Console()
    {
        var box = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = AppTheme.MonoFont,
            FontSize = 12.5,
        };
        box.Classes.Add("console");
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        return box;
    }

    public static void AppendLine(TextBox box, string text, int maxChars = 2_000_000)
    {
        string current = box.Text ?? "";
        if (current.Length > maxChars) current = current[^(maxChars / 2)..];
        box.Text = current + Privacy.MaskPaths(text).Replace("\r\n", "\n") + "\n";
        box.CaretIndex = box.Text.Length;
    }

    /// <summary>Empty cell (keeps grid columns aligned).</summary>
    public static Control Spacer() => new Border { Width = 1 };
}

/// <summary>Grid that grows by rows of auto-sized cells; the last column takes the remaining width.</summary>
internal sealed class FormGrid : Grid
{
    private readonly int _columns;

    public FormGrid(int columns)
    {
        _columns = columns;
        for (int i = 0; i < columns; i++)
            ColumnDefinitions.Add(new ColumnDefinition(i == columns - 1 ? GridLength.Star : GridLength.Auto));
    }

    public int RowCount => RowDefinitions.Count;

    public void AddRow(params Control[] controls)
    {
        int row = RowDefinitions.Count;
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int i = 0; i < controls.Length; i++)
        {
            SetRow(controls[i], row);
            SetColumn(controls[i], i);
            if (i == controls.Length - 1 && controls.Length < _columns)
                SetColumnSpan(controls[i], _columns - i);
            Children.Add(controls[i]);
        }
    }

    public void Clear()
    {
        Children.Clear();
        RowDefinitions.Clear();
    }
}

/// <summary>Rounded card with an optional title (replacement for GroupBox).</summary>
internal sealed class Card : Border
{
    private readonly StackPanel _stack = new();

    public Card(string de = "", string en = "")
    {
        Background = AppTheme.Card;
        BorderBrush = AppTheme.CardBorder;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(16, 12, 16, 14);
        Margin = new Thickness(0, 0, 0, 12);
        if (de.Length > 0)
        {
            var title = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };
            title.Classes.Add("section");
            _stack.Children.Add(Ui.Bind(title, de, en));
        }
        Child = _stack;
    }

    public void SetContent(Control content) => _stack.Children.Add(content);
}

/// <summary>"ADBora" with "ADB" bold in the accent colour.</summary>
internal sealed class BrandLabel : TextBlock
{
    public BrandLabel()
    {
        FontSize = AppTheme.TitleSize;
        Inlines = new Avalonia.Controls.Documents.InlineCollection
        {
            new Avalonia.Controls.Documents.Run("ADB") { FontWeight = FontWeight.Bold, Foreground = AppTheme.Accent },
            new Avalonia.Controls.Documents.Run("ora") { FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Text },
        };
    }
}
