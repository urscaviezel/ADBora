using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AdbTool.UI;

internal sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Errors in event handlers: log and show instead of closing ADBora
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += async (_, e) =>
        {
            e.Handled = true;
            Program.ReportCrash(e.Exception);
            try
            {
                Avalonia.Controls.Window? owner = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                await Dialogs.Message(owner, e.Exception.Message, "ADBora", MessageKind.Error);
            }
            catch { }
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Program.SmokeTest);
        base.OnFrameworkInitializationCompleted();
    }
}
