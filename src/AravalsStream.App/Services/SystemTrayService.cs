using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AravalsStream.Core.Versioning;

namespace AravalsStream.App.Services;

public sealed class SystemTrayService : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 101;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    private const int IDI_APPLICATION = 32512;

    private readonly Window _window;
    private readonly Action _onOpen;
    private readonly Action _onToggleStream;
    private readonly Action _onToggleRecord;
    private readonly Action _onToggleMuteMic;
    private readonly Action _onExit;

    private HwndSource? _hwndSource;
    private System.Drawing.Icon? _brandIcon;
    private bool _isAdded;
    private NOTIFYICONDATA _nid;

    public SystemTrayService(
        Window window,
        Action onOpen,
        Action onToggleStream,
        Action onToggleRecord,
        Action onToggleMuteMic,
        Action onExit)
    {
        _window = window;
        _onOpen = onOpen;
        _onToggleStream = onToggleStream;
        _onToggleRecord = onToggleRecord;
        _onToggleMuteMic = onToggleMuteMic;
        _onExit = onExit;
    }

    public void Initialize()
    {
        var helper = new WindowInteropHelper(_window);
        if (helper.Handle != IntPtr.Zero)
        {
            SetupTray(helper.Handle);
        }
        else
        {
            _window.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(_window).Handle;
                SetupTray(h);
            };
        }
    }

    private void SetupTray(IntPtr hwnd)
    {
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(TrayHook);

        _brandIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ResourceAssembly.Location);
        IntPtr hIcon = _brandIcon?.Handle ?? LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = AppVersion.Name
        };

        _isAdded = Shell_NotifyIcon(NIM_ADD, ref _nid);
    }

    private IntPtr TrayHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int mouseMsg = lParam.ToInt32();
            if (mouseMsg == WM_LBUTTONDBLCLK)
            {
                _onOpen();
                handled = true;
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "Open Aravals Stream" };
        openItem.Click += (_, _) => _onOpen();
        menu.Items.Add(openItem);

        menu.Items.Add(new Separator());

        var streamItem = new MenuItem { Header = "Start / Stop Stream" };
        streamItem.Click += (_, _) => _onToggleStream();
        menu.Items.Add(streamItem);

        var recItem = new MenuItem { Header = "Start / Stop Recording" };
        recItem.Click += (_, _) => _onToggleRecord();
        menu.Items.Add(recItem);

        var micItem = new MenuItem { Header = "Mute / Unmute Mic" };
        micItem.Click += (_, _) => _onToggleMuteMic();
        menu.Items.Add(micItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => _onExit();
        menu.Items.Add(exitItem);

        menu.IsOpen = true;
    }

    public void UpdateTooltip(string text)
    {
        if (_isAdded)
        {
            _nid.szTip = text.Length > 127 ? text.Substring(0, 127) : text;
            Shell_NotifyIcon(NIM_MODIFY, ref _nid);
        }
    }

    public void Dispose()
    {
        if (_isAdded)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _isAdded = false;
        }
        _hwndSource?.RemoveHook(TrayHook);
        _brandIcon?.Dispose();
        _brandIcon = null;
    }
}
