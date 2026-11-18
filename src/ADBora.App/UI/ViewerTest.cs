using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using AdbTool.Cast;
using AdbTool.Core;

namespace AdbTool.UI;

/// <summary>Diagnostic app (--viewer-test): runs a cast session in the real viewer window and saves what it shows.</summary>
internal sealed class ViewerTest : Application
{
    public static string[] Args = Array.Empty<string>();

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string serial = Args[0], png = Args[3];
        CastKind kind = Args[1] == "pfd" ? CastKind.PlayForDream : Args[1] == "pico" ? CastKind.Pico : CastKind.Quest;
        int seconds = int.Parse(Args[2]);
        string log = png + ".log";
        void L(string t) { lock (log) File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {t}\n"); }
        try { File.Delete(log); } catch { }
        try
        {
            string adb = AdbLocator.Discover("") ?? throw new FileNotFoundException("adb");
            var session = new CastSession(kind, adb, serial, ScrcpyTool.Find(""));
            session.Log += L;
            session.Ended += r => L("ended: " + (r ?? "-"));
            var viewer = new CastViewer("ADBora Stream – Test", false);
            await session.StartAsync(viewer, png + ".mkv", audio: true);
            L("started");
            await Task.Delay(seconds * 1000);
            var size = new PixelSize((int)viewer.Bounds.Width, (int)viewer.Bounds.Height);
            using (var rtb = new RenderTargetBitmap(size))
            {
                rtb.Render((Control)viewer.Content!);
#pragma warning disable CS0618
                rtb.Save(png);
#pragma warning restore CS0618
            }
            L($"window {size.Width}x{size.Height}, title: {viewer.Title}");
            await session.StopAsync();
            L("OK");
        }
        catch (Exception ex) { L("ERROR " + ex); }
        desktop.Shutdown();
    }
}
