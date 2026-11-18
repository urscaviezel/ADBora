using Avalonia.Media;

namespace AdbTool.UI;

/// <summary>Dark colour palette (taken over from the WinForms version) and fonts.</summary>
internal static class AppTheme
{
    private static SolidColorBrush B(string hex) => new(Color.Parse(hex));

    public static readonly IBrush Background = B("#111a28");
    public static readonly IBrush HeaderBackground = B("#0d1520");
    public static readonly IBrush HeaderHover = B("#18222f");
    public static readonly IBrush Card = B("#162234");
    public static readonly IBrush CardBorder = B("#2b394f");
    public static readonly IBrush Text = B("#e4ecf7");
    public static readonly IBrush Muted = B("#a8b4c6");
    public static readonly IBrush Button = B("#26364d");
    public static readonly IBrush ButtonBorder = B("#3a4c64");
    public static readonly IBrush ButtonHover = B("#344a65");
    public static readonly IBrush Primary = B("#217a69");
    public static readonly IBrush PrimaryBorder = B("#359b84");
    public static readonly IBrush PrimaryHover = B("#2a8f7b");
    public static readonly IBrush Danger = B("#8a3b3b");
    public static readonly IBrush DangerBorder = B("#b05454");
    public static readonly IBrush DisabledText = B("#738097");
    public static readonly IBrush DisabledBack = B("#1b2737");
    public static readonly IBrush DisabledBorder = B("#293548");
    public static readonly IBrush Input = B("#1b293b");
    public static readonly IBrush TableAlt = B("#1b293b");
    public static readonly IBrush Grid = B("#2b394f");
    public static readonly IBrush TableHeader = B("#233249");
    public static readonly IBrush Selection = B("#2e4860");
    public static readonly IBrush Console = B("#0e1622");
    public static readonly IBrush Accent = B("#67d5b5");
    public static readonly IBrush Warning = B("#e0b060");
    public static readonly IBrush Error = B("#ef7b7b");

    public const double BaseSize = 13;
    public const double SectionSize = 15;
    public const double TitleSize = 22;
    public const double TabSize = 13.5;

    /// <summary>Monospace font: Cascadia (Windows 11), then common Linux fonts.</summary>
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, JetBrains Mono, DejaVu Sans Mono, Noto Sans Mono, monospace");
}
