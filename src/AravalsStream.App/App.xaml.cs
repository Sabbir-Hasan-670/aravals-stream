using System.Windows;
using AravalsStream.Core.Services;

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
    }
}

