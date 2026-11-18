using Avalonia;
using Avalonia.Controls;

namespace AdbTool.UI;

internal static class Responsive
{
    /// <summary>
    /// Two columns side by side; stacked vertically when narrower than <paramref name="breakpoint"/> (device-independent pixels).
    /// </summary>
    public static Grid TwoColumns(Control first, Control second, double breakpoint, Thickness padding, double firstPercent = 50, Action<bool>? changed = null)
    {
        var grid = new Grid { Margin = padding };
        // only the gap between the columns is managed here; top/bottom margins of the children stay
        Thickness m1 = first.Margin, m2 = second.Margin;
        grid.Children.Add(first);
        grid.Children.Add(second);
        bool? narrowNow = null;
        void Apply(double width)
        {
            bool narrow = width < breakpoint;
            if (narrow == narrowNow) return;
            narrowNow = narrow;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            if (narrow)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetColumn(first, 0); Grid.SetRow(first, 0);
                Grid.SetColumn(second, 0); Grid.SetRow(second, 1);
                first.Margin = new Thickness(0, m1.Top, 0, m1.Bottom);
                second.Margin = new Thickness(0, m2.Top, 0, m2.Bottom);
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(firstPercent, GridUnitType.Star));
                grid.ColumnDefinitions.Add(new ColumnDefinition(100 - firstPercent, GridUnitType.Star));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetColumn(first, 0); Grid.SetRow(first, 0);
                Grid.SetColumn(second, 1); Grid.SetRow(second, 0);
                first.Margin = new Thickness(0, m1.Top, 10, m1.Bottom);
                second.Margin = new Thickness(10, m2.Top, 0, m2.Bottom);
            }
            changed?.Invoke(narrow);
        }
        Apply(double.MaxValue);
        grid.SizeChanged += (_, e) => Apply(e.NewSize.Width + padding.Left + padding.Right);
        return grid;
    }
}
