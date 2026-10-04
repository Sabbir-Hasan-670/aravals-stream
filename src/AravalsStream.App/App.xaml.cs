using System.Windows;
using System.Windows.Threading;
using AravalsStream.Core.Services;
using AravalsStream.App.Views;

namespace AravalsStream.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            AppLog.Write("Fatal", $"AppDomain Unhandled: {args.ExceptionObject}");
        };
        DispatcherUnhandledException += (s, args) =>
        {
            AppLog.Write("Fatal", $"Dispatcher Unhandled: {args.Exception}");
            args.Handled = false;
        };
        _ = OpenMainWindowAsync();
    }

    private async Task OpenMainWindowAsync()
    {
        var splash = new SplashWindow();
        splash.Show();
        await Dispatcher.Yield(DispatcherPriority.Render);

        try
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Loaded += (_, _) => { if (splash.IsVisible) splash.Close(); };
            mainWindow.Closed += (_, _) => { if (splash.IsVisible) splash.Close(); };
            mainWindow.Show();
        }
        catch
        {
            splash.Close();
            throw;
        }
    }
}
