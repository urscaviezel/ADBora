using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using AdbTool.Core;

namespace AdbTool.UI;

internal enum ButtonKind { Normal, Primary, Danger }

/// <summary>Flat, rounded dark button.</summary>
internal sealed class DarkButton : Button
{
    private bool _hover;
    private bool _pressed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get; set; }

    public DarkButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.BaseFont;
        ForeColor = Theme.Text;
        BackColor = Theme.Background;
        Cursor = Cursors.Hand;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        Padding = new Padding(12, 6, 12, 6);
        Margin = new Padding(0, 0, 8, 0);
        MinimumSize = new Size(0, 34);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = TextRenderer.MeasureText(Text, Font);
        return new Size(text.Width + Padding.Horizontal + Theme.S(8), Math.Max(Theme.S(34), text.Height + Padding.Vertical + Theme.S(6)));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    private bool _clicked;

    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; _clicked = false; Invalidate(); base.OnMouseDown(e); }

    protected override void OnClick(EventArgs e) { _clicked = true; base.OnClick(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool wasPressed = _pressed && e.Button == MouseButtons.Left;
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
        // ButtonBase only raises Click if WindowFromPoint() returns this button.
        // In the composited window this check can fail although the click is
        // clearly inside the button – raise the click ourselves in that case.
        if (wasPressed && !_clicked && Enabled && ClientRectangle.Contains(e.Location))
            OnClick(EventArgs.Empty);
    }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Background);

        Color back, border, fore;
        if (!Enabled)
        {
            back = Theme.DisabledBack; border = Theme.DisabledBorder; fore = Theme.DisabledText;
        }
        else
        {
            (back, border) = Kind switch
            {
                ButtonKind.Primary => (_hover ? Theme.PrimaryHover : Theme.Primary, Theme.PrimaryBorder),
                ButtonKind.Danger => (_hover ? ControlPaint.Light(Theme.Danger, 0.2f) : Theme.Danger, Theme.DangerBorder),
                _ => (_hover ? Theme.ButtonHover : Theme.Button, Theme.ButtonBorder)
            };
            if (_pressed) back = ControlPaint.Dark(back, 0.05f);
            fore = Theme.Text;
        }

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Theme.RoundedRect(rect, Theme.S(6)))
        using (var brush = new SolidBrush(back))
        using (var pen = new Pen(Focused && ShowFocusCues ? Theme.Accent : border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        Font font = Kind == ButtonKind.Primary ? Theme.BoldFont : Font;
        TextRenderer.DrawText(g, Text, font, rect, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Rounded card with an optional title (replacement for GroupBox).</summary>
internal sealed class Card : Panel
{
    private readonly Label _title;

    /// <summary>
    /// Paint the card and all children double-buffered in one pass
    /// (WS_EX_COMPOSITED). Only for cards without native edit/combo boxes,
    /// e.g. frequently updated status displays.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Composited { get; init; }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // The main window itself is composited; nested WS_EX_COMPOSITED is not needed.
            return cp;
        }
    }

    public Card(string de = "", string en = "")
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Padding = new Padding(16, de.Length > 0 ? 42 : 14, 16, 14);
        Margin = new Padding(0, 0, 0, 12);

        _title = new Label
        {
            AutoSize = true,
            Font = Theme.SectionFont,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Location = new Point(15, 11)
        };
        if (de.Length > 0)
        {
            Loc.Bind(_title, de, en);
            Controls.Add(_title);
        }
    }

    /// <summary>
    /// Adds the card content. With fill = false the card height follows the
    /// (auto-sized) content; with fill = true the content fills the card.
    /// </summary>
    public void SetContent(Control content, bool fill = false)
    {
        content.Dock = fill ? DockStyle.Fill : DockStyle.Top;
        Controls.Add(content);
        content.BringToFront();
        if (!fill)
        {
            void Fit() => Height = content.Height + Padding.Vertical;
            content.SizeChanged += (_, _) => Fit();
            Fit();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using GraphicsPath path = Theme.RoundedRect(rect, Theme.S(8));
        using var brush = new SolidBrush(Theme.Card);
        using var pen = new Pen(Theme.CardBorder);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }
}

/// <summary>Flat progress bar in the accent colour.</summary>
internal sealed class DarkProgressBar : Control
{
    private int _value;
    private int _maximum = 100;
    private bool _marquee;
    private int _marqueePos;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };

    public DarkProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 24;
        BackColor = Theme.Input;
        ForeColor = Theme.Text;
        Font = Theme.BaseFont;
        _timer.Tick += (_, _) => { _marqueePos = (_marqueePos + 6) % Math.Max(1, Width + Theme.S(120)); Invalidate(); };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowPercent { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum
    {
        get => _maximum;
        set { _maximum = Math.Max(1, value); _value = Math.Min(_value, _maximum); Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, 0, _maximum);
            if (v == _value) return;
            _value = v;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Marquee
    {
        get => _marquee;
        set { _marquee = value; _timer.Enabled = value; Invalidate(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Theme.RoundedRect(rect, Theme.S(5)))
        using (var brush = new SolidBrush(Theme.Input))
        using (var pen = new Pen(Theme.ButtonBorder))
        {
            g.FillPath(brush, path);
            g.SetClip(path);
            using var fill = new SolidBrush(Theme.Primary);
            if (_marquee)
            {
                g.FillRectangle(fill, new Rectangle(_marqueePos - Theme.S(120), 0, Theme.S(120), Height));
            }
            else
            {
                int w = (int)(Width * (double)_value / _maximum);
                g.FillRectangle(fill, new Rectangle(0, 0, w, Height));
            }
            g.ResetClip();
            g.DrawPath(pen, path);
        }

        if (ShowPercent && !_marquee)
        {
            string text = $"{(int)Math.Round(100.0 * _value / _maximum)} %";
            TextRenderer.DrawText(g, text, Font, rect, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

/// <summary>Top navigation: one flat "tab" per page.</summary>
internal sealed class TabStrip : Control
{
    private readonly List<(string De, string En, string Glyph)> _tabs = new();
    private readonly List<Rectangle> _bounds = new();
    private int _selected;
    private int _hover = -1;

    public event Action<int>? SelectedIndexChanged;

    private static readonly Font GlyphFont = new("Segoe MDL2 Assets", 12f);

    public TabStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Height = 48;
        BackColor = Theme.HeaderBackground;
        Font = Theme.TabFont;
        Loc.LanguageChanged += Invalidate;
    }

    public void AddTab(string glyph, string de, string en)
    {
        _tabs.Add((de, en, glyph));
        Invalidate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _tabs.Count || value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        Loc.LanguageChanged -= Invalidate;
        base.Dispose(disposing);
    }

    private bool _compact;

    private void LayoutTabs(Graphics g)
    {
        // Full layout (icon + text); if the window is too narrow, drop the
        // icons and reduce the padding so all tabs stay visible.
        foreach (bool compact in new[] { false, true })
        {
            _compact = compact;
            _bounds.Clear();
            int x = Theme.S(compact ? 8 : 16);
            foreach (var tab in _tabs)
            {
                Size size = TextRenderer.MeasureText(g, Loc.T(tab.De, tab.En), Font);
                var r = new Rectangle(x, Theme.S(6), size.Width + Theme.S(compact ? 20 : 58), Height - Theme.S(6));
                _bounds.Add(r);
                x = r.Right + Theme.S(compact ? 2 : 4);
            }
            if (x <= Width) break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        LayoutTabs(g);

        using (var line = new Pen(Theme.CardBorder))
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        for (int i = 0; i < _tabs.Count; i++)
        {
            Rectangle r = _bounds[i];
            bool selected = i == _selected;
            if (selected || i == _hover)
            {
                using GraphicsPath path = Theme.RoundedRect(new Rectangle(r.X, r.Y, r.Width, r.Height + Theme.S(8)), Theme.S(7));
                using var brush = new SolidBrush(selected ? Theme.Background : ControlPaint.Light(Theme.HeaderBackground, 0.15f));
                g.FillPath(brush, path);
            }
            if (selected)
            {
                using var accent = new SolidBrush(Theme.Accent);
                g.FillRectangle(accent, new Rectangle(r.X + Theme.S(10), r.Y, r.Width - Theme.S(20), Theme.S(3)));
            }

            Color color = selected ? Theme.Text : Theme.Muted;
            if (!_compact)
            {
                var glyphRect = new Rectangle(r.X + Theme.S(14), r.Y, Theme.S(22), r.Height);
                TextRenderer.DrawText(g, _tabs[i].Glyph, GlyphFont, glyphRect, selected ? Theme.Accent : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            var textRect = _compact
                ? new Rectangle(r.X + Theme.S(10), r.Y, r.Width - Theme.S(12), r.Height)
                : new Rectangle(r.X + Theme.S(40), r.Y, r.Width - Theme.S(44), r.Height);
            TextRenderer.DrawText(g, Loc.T(_tabs[i].De, _tabs[i].En), Font, textRect, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hover = _bounds.FindIndex(r => r.Contains(e.Location));
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int index = _bounds.FindIndex(r => r.Contains(e.Location));
        if (index >= 0) SelectedIndex = index;
        base.OnMouseDown(e);
    }
}

/// <summary>Custom painted check box / radio button for the dark theme.</summary>
internal sealed class DarkCheck : CheckBox
{
    public DarkCheck()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = true;
        Cursor = Cursors.Hand;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = TextRenderer.MeasureText(Text, Font);
        return new Size(text.Width + Theme.S(30), Math.Max(text.Height, Theme.S(20)) + Theme.S(4));
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e) => DarkToggle.Paint(this, e.Graphics, Checked, radio: false);
}

internal sealed class DarkRadio : RadioButton
{
    public DarkRadio()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = true;
        Cursor = Cursors.Hand;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = TextRenderer.MeasureText(Text, Font);
        return new Size(text.Width + Theme.S(30), Math.Max(text.Height, Theme.S(20)) + Theme.S(4));
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e) => DarkToggle.Paint(this, e.Graphics, Checked, radio: true);
}

internal static class DarkToggle
{
    public static void Paint(ButtonBase c, Graphics g, bool isChecked, bool radio)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color back = c.Parent is Card ? Theme.Card : FindBack(c);
        g.Clear(back);
        int s = Theme.S(18);
        var box = new Rectangle(1, (c.Height - s) / 2, s, s);
        bool enabled = c.Enabled;
        using (var fill = new SolidBrush(isChecked && enabled ? Theme.Primary : Theme.Input))
        using (var pen = new Pen(isChecked && enabled ? Theme.PrimaryBorder : Theme.ButtonBorder, Theme.S(1.4f)))
        {
            if (radio)
            {
                g.FillEllipse(fill, box);
                g.DrawEllipse(pen, box);
                if (isChecked)
                {
                    int d = s / 2 - 1;
                    using var dot = new SolidBrush(enabled ? Color.White : Theme.DisabledText);
                    g.FillEllipse(dot, box.X + (s - d) / 2f, box.Y + (s - d) / 2f, d, d);
                }
            }
            else
            {
                using GraphicsPath path = Theme.RoundedRect(box, Theme.S(4));
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
                if (isChecked)
                {
                    float k = s / 20f;
                    using var tick = new Pen(enabled ? Color.White : Theme.DisabledText, Theme.S(2.2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLines(tick, new[] { new PointF(box.X + 5 * k, box.Y + 10.5f * k), new PointF(box.X + 8.5f * k, box.Y + 14 * k), new PointF(box.X + 15 * k, box.Y + 6.5f * k) });
                }
            }
        }
        var textRect = new Rectangle(box.Right + Theme.S(8), 0, c.Width - box.Right - Theme.S(8), c.Height);
        TextRenderer.DrawText(g, c.Text, c.Font, textRect, enabled ? Theme.Text : Theme.DisabledText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        if (c.Focused)
        {
            using var focus = new Pen(Theme.Accent) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 0, 0, c.Width - 1, c.Height - 1);
        }
    }

    private static Color FindBack(Control c)
    {
        for (Control? p = c.Parent; p is not null; p = p.Parent)
        {
            if (p is Card) return Theme.Card;
            if (p.BackColor != Color.Transparent) return p.BackColor;
        }
        return Theme.Background;
    }
}

/// <summary>
/// Flat drop-down list (replacement for ComboBox, which does not render its
/// text under WS_EX_COMPOSITED). The list opens as a dark context menu.
/// </summary>
internal sealed class DarkComboBox : Control
{
    private int _selectedIndex = -1;
    private bool _hover;
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false, ShowCheckMargin = false };

    public List<object> Items { get; } = new();

    public event EventHandler? SelectedIndexChanged;

    /// <summary>Raised only when the user picks an entry.</summary>
    public event EventHandler? SelectionChangeCommitted;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int DropDownWidth { get; set; }

    public DarkComboBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Theme.BaseFont;
        ForeColor = Theme.Text;
        BackColor = Theme.Input;
        Height = 30;
        Cursor = Cursors.Hand;
        TabStop = true;
        _menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false };
        _menu.BackColor = Theme.Input;
        _menu.ForeColor = Theme.Text;
        _menu.Font = Theme.BaseFont;
    }

    public void BeginUpdate() { }
    public void EndUpdate() => Invalidate();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int v = value < -1 || value >= Items.Count ? -1 : value;
            if (v == _selectedIndex) { Invalidate(); return; }
            _selectedIndex = v;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? SelectedItem => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && Enabled)
        {
            Focus();
            OpenList();
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled || Items.Count == 0) return;
        if (e.KeyCode == Keys.Down && _selectedIndex < Items.Count - 1) Commit(_selectedIndex + 1);
        else if (e.KeyCode == Keys.Up && _selectedIndex > 0) Commit(_selectedIndex - 1);
        else if (e.KeyCode is Keys.Space or Keys.Enter || (e.Alt && e.KeyCode == Keys.Down)) OpenList();
    }

    private void Commit(int index)
    {
        SelectedIndex = index;
        SelectionChangeCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void OpenList()
    {
        if (Items.Count == 0) return;
        _menu.Items.Clear();
        int width = Math.Max(Width, DropDownWidth);
        for (int i = 0; i < Items.Count; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(Items[i]?.ToString() ?? "")
            {
                AutoSize = false,
                Size = new Size(width - 2, Theme.S(28)),
                ForeColor = Theme.Text,
                Font = i == _selectedIndex ? Theme.BoldFont : Theme.BaseFont,
                TextAlign = ContentAlignment.MiddleLeft
            };
            item.Click += (_, _) => Commit(index);
            _menu.Items.Add(item);
        }
        _menu.Show(this, new Point(0, Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent is null ? Theme.Background : ParentBack(this));
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color border = !Enabled ? Theme.DisabledBorder : (Focused || _hover) ? Theme.Accent : Theme.ButtonBorder;
        using (GraphicsPath path = Theme.RoundedRect(rect, Theme.S(5)))
        using (var brush = new SolidBrush(Enabled ? Theme.Input : Theme.DisabledBack))
        using (var pen = new Pen(border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        int arrow = Theme.S(10);
        var textRect = new Rectangle(Theme.S(8), 0, Width - Theme.S(8) - arrow - Theme.S(16), Height);
        TextRenderer.DrawText(g, SelectedItem?.ToString() ?? "", Font, textRect, Enabled ? Theme.Text : Theme.DisabledText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

        int cx = Width - Theme.S(16), cy = Height / 2;
        using var arrowPen = new Pen(Enabled ? Theme.Muted : Theme.DisabledText, Theme.S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(arrowPen, new[] { new PointF(cx - arrow / 2f, cy - arrow / 4f), new PointF(cx, cy + arrow / 4f), new PointF(cx + arrow / 2f, cy - arrow / 4f) });
    }

    private static Color ParentBack(Control c)
    {
        for (Control? p = c.Parent; p is not null; p = p.Parent)
        {
            if (p is Card) return Theme.Card;
            if (p.BackColor != Color.Transparent) return p.BackColor;
        }
        return Theme.Background;
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Input;
        public override Color MenuBorder => Theme.ButtonBorder;
        public override Color MenuItemBorder => Theme.Selection;
        public override Color MenuItemSelected => Theme.Selection;
        public override Color MenuItemSelectedGradientBegin => Theme.Selection;
        public override Color MenuItemSelectedGradientEnd => Theme.Selection;
        public override Color ImageMarginGradientBegin => Theme.Input;
        public override Color ImageMarginGradientMiddle => Theme.Input;
        public override Color ImageMarginGradientEnd => Theme.Input;
    }
}

/// <summary>Drag &amp; drop target (files / folders); a click opens a picker instead.</summary>
internal sealed class DropZone : Control
{
    private readonly string _glyph;
    private readonly (string De, string En) _title;
    private readonly (string De, string En) _subtitle;
    private bool _dragOver;
    private bool _hover;
    private static readonly Font GlyphFont = new("Segoe MDL2 Assets", 22f);

    /// <summary>Validates dropped paths; return true if they are accepted.</summary>
    public Func<string[], bool>? Accepts { get; set; }

    public event Action<string[]>? Dropped;

    public DropZone(string glyph, string titleDe, string titleEn, string subDe, string subEn)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _glyph = glyph;
        _title = (titleDe, titleEn);
        _subtitle = (subDe, subEn);
        AllowDrop = true;
        Height = 150;
        Cursor = Cursors.Hand;
        Font = Theme.BaseFont;
        Loc.LanguageChanged += Invalidate;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Loc.LanguageChanged -= Invalidate;
        base.Dispose(disposing);
    }

    private static string[]? Paths(DragEventArgs e) =>
        e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    protected override void OnDragEnter(DragEventArgs e)
    {
        string[]? paths = Paths(e);
        bool ok = Enabled && paths is { Length: > 0 } && (Accepts?.Invoke(paths) ?? true);
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        _dragOver = ok;
        Invalidate();
        base.OnDragEnter(e);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        _dragOver = false;
        Invalidate();
        base.OnDragLeave(e);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        _dragOver = false;
        Invalidate();
        base.OnDragDrop(e);
        string[]? paths = Paths(e);
        if (Enabled && paths is { Length: > 0 } && (Accepts?.Invoke(paths) ?? true))
            BeginInvoke(new Action(() => Dropped?.Invoke(paths)));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Card);
        var rect = new Rectangle(Theme.S(1), Theme.S(1), Width - Theme.S(3), Height - Theme.S(3));
        Color accent = !Enabled ? Theme.DisabledBorder : _dragOver ? Theme.Accent : _hover ? Theme.PrimaryBorder : Theme.ButtonBorder;
        using (GraphicsPath path = Theme.RoundedRect(rect, Theme.S(10)))
        using (var fill = new SolidBrush(_dragOver ? Color.FromArgb(40, Theme.Accent) : Theme.Input))
        using (var pen = new Pen(accent, Theme.S(1.6f)) { DashStyle = _dragOver ? DashStyle.Solid : DashStyle.Dash })
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        Color text = Enabled ? Theme.Text : Theme.DisabledText;
        int y = Height / 2;
        TextRenderer.DrawText(g, _glyph, GlyphFont, new Rectangle(0, y - Theme.S(58), Width, Theme.S(40)),
            Enabled ? (_dragOver ? Theme.Accent : Theme.Muted) : Theme.DisabledText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Loc.T(_title.De, _title.En), Theme.BoldFont, new Rectangle(Theme.S(8), y - Theme.S(12), Width - Theme.S(16), Theme.S(24)),
            text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Loc.T(_subtitle.De, _subtitle.En), Theme.BaseFont, new Rectangle(Theme.S(12), y + Theme.S(12), Width - Theme.S(24), Height - y - Theme.S(16)),
            Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Product name "ADBora" with the "ADB" part highlighted.</summary>
internal sealed class BrandLabel : Control
{
    private static readonly Font StrongFont = new("Segoe UI", 17f, FontStyle.Bold);
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.Left | TextFormatFlags.Top;

    public BrandLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Theme.TitleFont;
        Size = GetPreferredSize(Size.Empty);
    }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        // Fonts are already DPI-aware; only move the control, size follows the text.
        base.ScaleControl(factor, specified & ~BoundsSpecified.Size);
        Size = GetPreferredSize(Size.Empty);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size a = TextRenderer.MeasureText("ADB", StrongFont, Size.Empty, Flags);
        Size b = TextRenderer.MeasureText("ora", Font, Size.Empty, Flags);
        return new Size(a.Width + b.Width + 2, Math.Max(a.Height, b.Height) + 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Size a = TextRenderer.MeasureText(e.Graphics, "ADB", StrongFont, Size.Empty, Flags);
        Size b = TextRenderer.MeasureText(e.Graphics, "ora", Font, Size.Empty, Flags);
        int baseline = Math.Max(a.Height, b.Height);
        TextRenderer.DrawText(e.Graphics, "ADB", StrongFont, new Point(0, baseline - a.Height), Theme.Accent, Flags);
        TextRenderer.DrawText(e.Graphics, "ora", Font, new Point(a.Width, baseline - b.Height), Theme.Text, Flags);
    }
}

/// <summary>Factory helpers so every page looks the same.</summary>
internal static class Ui
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

    private const int WM_SETREDRAW = 0x000B;

    /// <summary>Stops all drawing of a control (and its children) until <see cref="EndFreeze"/>.</summary>
    public static bool BeginFreeze(Control control)
    {
        if (!control.IsHandleCreated || !control.Visible) return false;
        SendMessage(control.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    /// <summary>Re-enables drawing and repaints the control with all children in one pass.</summary>
    public static void EndFreeze(Control control, bool frozen)
    {
        if (!frozen || !control.IsHandleCreated) return;
        SendMessage(control.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
        // RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW
        RedrawWindow(control.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0400 | 0x0080 | 0x0100);
    }

    /// <summary>Runs an update without intermediate repaints (no flicker).</summary>
    public static void Frozen(Control control, Action update)
    {
        bool frozen = BeginFreeze(control);
        try { update(); }
        finally { EndFreeze(control, frozen); }
    }

    /// <summary>
    /// Two-column layout that stacks both columns vertically when the page
    /// is narrower than <paramref name="breakpoint"/> (96-dpi pixels).
    /// </summary>
    public static void Responsive(Control page, TableLayoutPanel root, Control first, Control second, int breakpoint,
        float firstPercent = 50, Action<bool>? changed = null)
    {
        bool? current = null;
        void Apply()
        {
            if (page.ClientSize.Width <= 0) return;
            bool narrow = page.ClientSize.Width < Theme.S(breakpoint);
            if (narrow == current) return;
            current = narrow;

            root.SuspendLayout();
            root.ColumnStyles.Clear();
            root.RowStyles.Clear();
            if (narrow)
            {
                root.ColumnCount = 1;
                root.RowCount = 2;
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.SetCellPosition(first, new TableLayoutPanelCellPosition(0, 0));
                root.SetCellPosition(second, new TableLayoutPanelCellPosition(0, 1));
            }
            else
            {
                root.ColumnCount = 2;
                root.RowCount = 1;
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, firstPercent));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 - firstPercent));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.SetCellPosition(first, new TableLayoutPanelCellPosition(0, 0));
                root.SetCellPosition(second, new TableLayoutPanelCellPosition(1, 0));
            }
            root.ResumeLayout(true);
            changed?.Invoke(narrow);
        }
        page.Resize += (_, _) => Apply();
        page.VisibleChanged += (_, _) => Apply();
    }

    /// <summary>Keeps wrapping labels as wide as their container.</summary>
    public static void WrapTo(Label label, Control container, int reserve = 0)
    {
        void Apply()
        {
            int width = container.ClientSize.Width - container.Padding.Horizontal - label.Margin.Horizontal - Theme.S(reserve);
            if (width > 50) label.MaximumSize = new Size(width, 0);
        }
        container.Resize += (_, _) => Apply();
        Apply();
    }

    /// <summary>
    /// Shows a dialog created with <see cref="Dialog"/>: scales the 96-dpi
    /// paddings/margins of its buttons first (the dialog does not autoscale).
    /// </summary>
    public static DialogResult ShowDialog(Form dialog, IWin32Window? owner)
    {
        static IEnumerable<Control> All(Control root) =>
            root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(All(c)));

        foreach (DarkButton b in All(dialog).OfType<DarkButton>().ToList())
        {
            try
            {
                b.Padding = new Padding(Theme.S(b.Padding.Left), Theme.S(b.Padding.Top), Theme.S(b.Padding.Right), Theme.S(b.Padding.Bottom));
                b.Margin = new Padding(Theme.S(b.Margin.Left), Theme.S(b.Margin.Top), Theme.S(b.Margin.Right), Theme.S(b.Margin.Bottom));
                b.Size = b.GetPreferredSize(Size.Empty);
            }
            catch { }
        }
        return dialog.ShowDialog(owner);
    }

    /// <summary>Dark, DPI-scaled dialog window (sizes in 96-dpi pixels).</summary>
    public static Form Dialog(string title, int width, int height)
    {
        var dialog = new Form
        {
            AutoScaleMode = AutoScaleMode.None, // sizes are scaled explicitly with Theme.S
            Text = title,
            ClientSize = new Size(Theme.S(width), Theme.S(height)),
            MinimumSize = new Size(Theme.S(Math.Min(width, 420)), Theme.S(Math.Min(height, 200))),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Background,
            ForeColor = Theme.Text,
            Font = Theme.BaseFont,
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = false,
            MinimizeBox = false,
            Padding = new Padding(Theme.S(22))
        };
        dialog.HandleCreated += (_, _) => Theme.UseDarkTitleBar(dialog);
        return dialog;
    }

    public static Label Label(string de, string en, bool muted = false, bool bold = false)
    {
        var label = new Label
        {
            AutoSize = true,
            ForeColor = muted ? Theme.Muted : Theme.Text,
            Font = bold ? Theme.BoldFont : Theme.BaseFont,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 8, 10, 4)
        };
        return Loc.Bind(label, de, en);
    }

    public static Label Value(string text = "-")
    {
        return new Label
        {
            AutoSize = true,
            Text = text,
            ForeColor = Theme.Text,
            Font = Theme.BaseFont,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 8, 0, 4)
        };
    }

    public static DarkButton Button(string de, string en, ButtonKind kind = ButtonKind.Normal)
    {
        var button = new DarkButton { Kind = kind };
        return Loc.Bind(button, de, en);
    }

    public static TextBox TextBox(int width = 120)
    {
        var box = new TextBox
        {
            Width = width,
            BackColor = Theme.Input,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.BaseFont,
            Margin = new Padding(0, 4, 8, 4)
        };
        return box;
    }

    public static DarkComboBox Combo(int width = 200) => new() { Width = width, Margin = new Padding(0, 4, 8, 4) };

    /// <summary>
    /// Placeholder text as a child label of the text box. (The built-in
    /// PlaceholderText is not drawn when the window is double-buffered with
    /// WS_EX_COMPOSITED.)
    /// </summary>
    public static void Placeholder(TextBox box, string de, string en)
    {
        var hint = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            ForeColor = Theme.DisabledText,
            BackColor = box.BackColor,
            Font = box.Font,
            Cursor = Cursors.IBeam,
            Location = new Point(1, 1),
            Margin = Padding.Empty,
            UseMnemonic = false
        };
        Loc.Bind(hint, de, en);
        box.Controls.Add(hint);
        void Update() => hint.Visible = box.TextLength == 0 && !box.Focused;
        box.TextChanged += (_, _) => Update();
        box.GotFocus += (_, _) => Update();
        box.LostFocus += (_, _) => Update();
        void Fit() => hint.Size = new Size(Math.Max(10, box.ClientSize.Width - 2), Math.Max(10, box.ClientSize.Height - 2));
        box.Resize += (_, _) => Fit();
        Fit();
        box.FontChanged += (_, _) => { hint.Font = box.Font; Fit(); };
        box.BackColorChanged += (_, _) => hint.BackColor = box.BackColor;
        box.EnabledChanged += (_, _) => hint.BackColor = box.BackColor;
        hint.MouseDown += (_, _) => box.Focus();
        Update();
    }

    public static CheckBox CheckBox(string de, string en)
    {
        var box = new DarkCheck
        {
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Font = Theme.BaseFont,
            Margin = new Padding(0, 6, 16, 4)
        };
        return Loc.Bind(box, de, en);
    }

    public static RadioButton Radio(string de, string en)
    {
        var radio = new DarkRadio
        {
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Font = Theme.BaseFont,
            Margin = new Padding(0, 6, 20, 4)
        };
        return Loc.Bind(radio, de, en);
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6)
        };
        row.Controls.AddRange(controls);
        return row;
    }

    public static TextBox Console()
    {
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Theme.Console,
            ForeColor = Theme.Muted,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.MonoFont,
            MaxLength = 0
        };
        Theme.UseDarkScrollBars(box);
        return box;
    }

    public static void AppendLine(TextBox box, string text, int maxChars = 2_000_000)
    {
        if (box.TextLength > maxChars)
        {
            box.Text = box.Text[^(maxChars / 2)..];
        }
        box.AppendText(text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + Environment.NewLine);
    }

    public static TableLayoutPanel Grid(int columns)
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = columns,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Dock = DockStyle.Top
        };
        for (int i = 0; i < columns; i++)
            grid.ColumnStyles.Add(new ColumnStyle(i == columns - 1 ? SizeType.Percent : SizeType.AutoSize, i == columns - 1 ? 100 : 0));
        return grid;
    }

    public static void AddRow(TableLayoutPanel grid, params Control[] controls)
    {
        int row = grid.RowCount;
        grid.RowCount = row + 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int i = 0; i < controls.Length; i++)
            grid.Controls.Add(controls[i], i, row);
    }
}
