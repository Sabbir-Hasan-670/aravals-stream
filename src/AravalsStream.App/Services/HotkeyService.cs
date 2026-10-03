using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;

namespace AravalsStream.App.Services;

public sealed class HotkeyService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private readonly Window _window;
    private HwndSource? _source;
    private readonly Dictionary<int, HotkeyAction> _registeredHotkeys = [];

    public event Action<HotkeyAction>? HotkeyTriggered;

    public HotkeyService(Window window)
    {
        _window = window;
    }

    public void Initialize()
    {
        var helper = new WindowInteropHelper(_window);
        if (helper.Handle != IntPtr.Zero)
        {
            AttachHook(helper.Handle);
        }
        else
        {
            _window.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(_window).Handle;
                AttachHook(h);
            };
        }
    }

    private void AttachHook(IntPtr handle)
    {
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(HwndHook);
    }

    public void RegisterHotkeys(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterAll();
        var helper = new WindowInteropHelper(_window);
        if (helper.Handle == IntPtr.Zero) return;

        int id = 9000;
        foreach (var binding in bindings)
        {
            if (!binding.Enabled || binding.Key == 0) continue;

            if (RegisterHotKey(helper.Handle, id, (uint)binding.Modifiers, (uint)binding.Key))
            {
                _registeredHotkeys[id] = binding.Action;
                id++;
            }
            else
            {
                AppLog.Write("Hotkeys", $"Failed to register hotkey {binding.ShortcutText} for {binding.DisplayName}");
            }
        }
    }

    public void UnregisterAll()
    {
        var helper = new WindowInteropHelper(_window);
        if (helper.Handle != IntPtr.Zero)
        {
            foreach (var id in _registeredHotkeys.Keys)
            {
                UnregisterHotKey(helper.Handle, id);
            }
        }
        _registeredHotkeys.Clear();
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registeredHotkeys.TryGetValue(id, out var action))
            {
                HotkeyTriggered?.Invoke(action);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(HwndHook);
    }
}
