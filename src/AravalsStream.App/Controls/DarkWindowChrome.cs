using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AravalsStream.App.Controls;

public static class DarkWindowChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        window.Background = (Brush)Application.Current.Resources["ShellBrush"];
        window.Foreground = (Brush)Application.Current.Resources["TextBrush"];
        window.SourceInitialized += (_, _) =>
        {
            var enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
        };
    }
}
