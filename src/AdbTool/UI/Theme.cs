using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AdbTool.UI;

/// <summary>Dark colour palette (taken over from the former APK Backup Tool).</summary>
internal static class Theme
{
    public static readonly Color Background = ColorTranslator.FromHtml("#111a28");
    public static readonly Color HeaderBackground = ColorTranslator.FromHtml("#0d1520");
    public static readonly Color Card = ColorTranslator.FromHtml("#162234");
    public static readonly Color CardBorder = ColorTranslator.FromHtml("#2b394f");
    public static readonly Color Text = ColorTranslator.FromHtml("#e4ecf7");
    public static readonly Color Muted = ColorTranslator.FromHtml("#a8b4c6");
    public static readonly Color Button = ColorTranslator.FromHtml("#26364d");
    public static readonly Color ButtonBorder = ColorTranslator.FromHtml("#3a4c64");
    public static readonly Color ButtonHover = ColorTranslator.FromHtml("#344a65");
    public static readonly Color Primary = ColorTranslator.FromHtml("#217a69");
    public static readonly Color PrimaryBorder = ColorTranslator.FromHtml("#359b84");
    public static readonly Color PrimaryHover = ColorTranslator.FromHtml("#2a8f7b");
    public static readonly Color Danger = ColorTranslator.FromHtml("#8a3b3b");
    public static readonly Color DangerBorder = ColorTranslator.FromHtml("#b05454");
    public static readonly Color DisabledText = ColorTranslator.FromHtml("#738097");
    public static readonly Color DisabledBack = ColorTranslator.FromHtml("#1b2737");
    public static readonly Color DisabledBorder = ColorTranslator.FromHtml("#293548");
    public static readonly Color Input = ColorTranslator.FromHtml("#1b293b");
    public static readonly Color TableAlt = ColorTranslator.FromHtml("#1b293b");
    public static readonly Color Grid = ColorTranslator.FromHtml("#2b394f");
    public static readonly Color TableHeader = ColorTranslator.FromHtml("#233249");
    public static readonly Color Selection = ColorTranslator.FromHtml("#2e4860");
    public static readonly Color Console = ColorTranslator.FromHtml("#0e1622");
    public static readonly Color Accent = ColorTranslator.FromHtml("#67d5b5");
    public static readonly Color Warning = ColorTranslator.FromHtml("#e0b060");
    public static readonly Color Error = ColorTranslator.FromHtml("#ef7b7b");

    public static readonly Font BaseFont = new("Segoe UI", 9.75f);
    public static readonly Font BoldFont = new("Segoe UI Semibold", 9.75f);
    public static readonly Font TitleFont = new("Segoe UI Semibold", 17f);
    public static readonly Font SectionFont = new("Segoe UI Semibold", 11.5f);
    public static readonly Font TabFont = new("Segoe UI Semibold", 10.5f);
    public static readonly Font MonoFont = new("Cascadia Mono", 9.75f);

    static Theme()
    {
        // Cascadia Mono ships with Windows 11 / Terminal; fall back to Consolas.
        if (MonoFont.Name != "Cascadia Mono")
            MonoFont = new Font("Consolas", 10f);
    }

    /// <summary>DPI factor of the primary screen (1.0 = 96 dpi).</summary>
    public static float Scale { get; private set; } = 1f;

    /// <summary>Scales a 96-dpi pixel value for custom painting.</summary>
    public static int S(int value) => (int)Math.Round(value * Scale);
    public static float S(float value) => value * Scale;

    public static void InitScale()
    {
        try
        {
            using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
            Scale = Math.Max(1f, g.DpiX / 96f);
        }
        catch { Scale = 1f; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    /// <summary>Dark title bar on Windows 10 (1809+) and Windows 11.</summary>
    public static void UseDarkTitleBar(Form form)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
        }
        catch { }
    }

    /// <summary>Dark scroll bars for native controls (TextBox, ListView, DataGridView …).</summary>
    public static void UseDarkScrollBars(Control control)
    {
        void Apply()
        {
            try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); } catch { }
        }

        if (control.IsHandleCreated) Apply();
        else control.HandleCreated += (_, _) => Apply();
    }

    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
