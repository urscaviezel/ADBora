using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace AdbTool.UI;

/// <summary>
/// Scroll host for pages whose last element fills the remaining height (log, output, app list).
/// While the window is high enough the page fills it exactly; when it is lower, the filling element keeps
/// its MinHeight and the page scrolls instead of being cut off.
/// </summary>
internal static class FillScroll
{
    public static ScrollViewer Wrap(DockPanel page) => new()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        Content = new Host { Child = page },
    };

    private sealed class Host : Decorator
    {
        private ScrollViewer? _scroller;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _scroller = this.FindAncestorOfType<ScrollViewer>();
            if (_scroller is not null)
                _scroller.SizeChanged += (_, _) => InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size available)
        {
            if (Child is not DockPanel page) return base.MeasureOverride(available);
            double viewport = _scroller?.Bounds.Height ?? 0;
            if (double.IsNaN(viewport) || viewport <= 0) viewport = double.IsInfinity(available.Height) ? 0 : available.Height;
            double width = double.IsInfinity(available.Width) ? (_scroller?.Bounds.Width ?? 800) : available.Width;
            double height = Math.Max(viewport, MinimumHeight(page, width));
            page.Measure(new Size(width, height));
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            Child?.Arrange(new Rect(final));
            return final;
        }

        /// <summary>Docked parts at their natural height + the MinHeight of the filling (last) element.</summary>
        private static double MinimumHeight(DockPanel page, double width)
        {
            Thickness pm = page.Margin;
            double inner = Math.Max(0, width - pm.Left - pm.Right);
            double sum = pm.Top + pm.Bottom;
            int last = page.Children.Count - 1;
            for (int i = 0; i < page.Children.Count; i++)
            {
                Control c = page.Children[i];
                if (!c.IsVisible) continue;
                if (i == last && page.LastChildFill)
                {
                    sum += c.MinHeight + c.Margin.Top + c.Margin.Bottom;
                    continue;
                }
                Dock dock = DockPanel.GetDock(c);
                if (dock is Dock.Left or Dock.Right) continue;
                c.Measure(new Size(inner, double.PositiveInfinity));
                sum += c.DesiredSize.Height;
            }
            return sum;
        }
    }
}
