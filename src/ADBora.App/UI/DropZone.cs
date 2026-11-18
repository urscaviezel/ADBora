using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AdbTool.Core;

namespace AdbTool.UI;

/// <summary>Drag &amp; drop target (files / folders); a click opens a picker instead.</summary>
internal sealed class DropZone : Control
{
    private readonly (string De, string En) _title;
    private readonly (string De, string En) _subtitle;
    private bool _dragOver;
    private bool _hover;

    /// <summary>Validates dropped paths; return true if they are accepted.</summary>
    public Func<string[], bool>? Accepts { get; set; }

    public event Action<string[]>? Dropped;
    public event Action? Click;

    public DropZone(string titleDe, string titleEn, string subDe, string subEn)
    {
        _title = (titleDe, titleEn);
        _subtitle = (subDe, subEn);
        Height = 150;
        Cursor = new Cursor(StandardCursorType.Hand);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { _dragOver = false; InvalidateVisual(); });
        AddHandler(DragDrop.DropEvent, OnDrop);
        PointerEntered += (_, _) => { _hover = true; InvalidateVisual(); };
        PointerExited += (_, _) => { _hover = false; InvalidateVisual(); };
        PointerReleased += (_, e) => { if (IsEffectivelyEnabled && e.InitialPressMouseButton == MouseButton.Left) Click?.Invoke(); };
        Loc.LanguageChanged += InvalidateVisual;
        PropertyChanged += (_, e) => { if (e.Property == IsEnabledProperty) InvalidateVisual(); };
    }

    private static string[] Paths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray() ?? Array.Empty<string>();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        string[] paths = Paths(e);
        bool ok = IsEffectivelyEnabled && paths.Length > 0 && (Accepts?.Invoke(paths) ?? true);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok != _dragOver) { _dragOver = ok; InvalidateVisual(); }
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dragOver = false;
        InvalidateVisual();
        string[] paths = Paths(e);
        if (IsEffectivelyEnabled && paths.Length > 0 && (Accepts?.Invoke(paths) ?? true))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Dropped?.Invoke(paths));
    }

    public override void Render(DrawingContext g)
    {
        bool enabled = IsEffectivelyEnabled;
        var rect = new Rect(Bounds.Size).Deflate(1);
        IBrush accent = !enabled ? AppTheme.DisabledBorder : _dragOver ? AppTheme.Accent : _hover ? AppTheme.PrimaryBorder : AppTheme.ButtonBorder;
        var pen = new Pen(accent, 1.6, dashStyle: _dragOver ? null : new DashStyle(new double[] { 4, 3 }, 0));
        g.DrawRectangle(_dragOver ? new SolidColorBrush(Color.FromArgb(40, 0x67, 0xd5, 0xb5)) : AppTheme.Input, pen, rect, 10, 10);

        double y = Bounds.Height / 2;
        // arrow-into-tray glyph
        var glyphPen = new Pen(enabled ? (_dragOver ? AppTheme.Accent : AppTheme.Muted) : AppTheme.DisabledText, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        double cx = Bounds.Width / 2, top = y - 52;
        g.DrawLine(glyphPen, new Point(cx, top), new Point(cx, top + 20));
        g.DrawLine(glyphPen, new Point(cx - 7, top + 13), new Point(cx, top + 20));
        g.DrawLine(glyphPen, new Point(cx + 7, top + 13), new Point(cx, top + 20));
        g.DrawLine(glyphPen, new Point(cx - 13, top + 18), new Point(cx - 13, top + 26));
        g.DrawLine(glyphPen, new Point(cx - 13, top + 26), new Point(cx + 13, top + 26));
        g.DrawLine(glyphPen, new Point(cx + 13, top + 26), new Point(cx + 13, top + 18));

        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var title = new FormattedText(Loc.T(_title.De, _title.En), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, AppTheme.BaseSize + 0.5, enabled ? AppTheme.Text : AppTheme.DisabledText)
        { TextAlignment = TextAlignment.Center, MaxTextWidth = Math.Max(10, Bounds.Width - 16), Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
        g.DrawText(title, new Point(8, y - 10));
        var sub = new FormattedText(Loc.T(_subtitle.De, _subtitle.En), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), AppTheme.BaseSize - 0.5, AppTheme.Muted)
        { TextAlignment = TextAlignment.Center, MaxTextWidth = Math.Max(10, Bounds.Width - 24), MaxTextHeight = Math.Max(10, Bounds.Height - y - 18) };
        g.DrawText(sub, new Point(12, y + 14));
    }
}
