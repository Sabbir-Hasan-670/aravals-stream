using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Device = SharpDX.Direct3D11.Device;

namespace AravalsStream.Capture.Window;

public sealed record WindowInfo(nint Handle, string Title, string ProcessName, int Width, int Height)
{
    public string Id => Handle.ToString("X");
    public string Label => $"{Title}  ·  {ProcessName}  ·  {Width}×{Height}";
}

public interface IWindowCaptureService
{
    IReadOnlyList<WindowInfo> EnumerateWindows();
    WindowInfo? GetWindowInfo(nint hwnd, bool allowMinimized = false, string? expectedProcessName = null);
    IWindowCaptureSession Start(WindowInfo window);
}

public interface IWindowCaptureSession : Video.IVideoCaptureSession
{
    int TargetFps { get; set; }
}

public sealed class WindowCaptureService : IWindowCaptureService
{
    public IReadOnlyList<WindowInfo> EnumerateWindows()
    {
        var windows = new List<WindowInfo>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (GetWindowInfo(hwnd) is { } window) windows.Add(window);
            return true;
        }, 0);
        return windows.OrderBy(w => w.ProcessName).ThenBy(w => w.Title).ToList();
    }

    public WindowInfo? GetWindowInfo(nint hwnd, bool allowMinimized = false, string? expectedProcessName = null)
    {
        if (hwnd == 0 || !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd) ||
            (!allowMinimized && Native.IsIconic(hwnd)) || Native.GetWindow(hwnd, 4) != 0 ||
            (Native.GetWindowLongPtr(hwnd, -20).ToInt64() & 0x80) != 0)
            return null;
        var length = Native.GetWindowTextLength(hwnd);
        if (length <= 0) return null;
        var title = new StringBuilder(length + 1);
        Native.GetWindowText(hwnd, title, title.Capacity);
        Native.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == Environment.ProcessId) return null;
        string processName;
        try { processName = Process.GetProcessById((int)processId).ProcessName; }
        catch { processName = "Unknown"; }
        if (!string.IsNullOrWhiteSpace(expectedProcessName) &&
            !string.Equals(processName, expectedProcessName, StringComparison.OrdinalIgnoreCase))
            return null;
        var width = 0;
        var height = 0;
        if (Native.GetWindowRect(hwnd, out var bounds))
        {
            width = bounds.Right - bounds.Left;
            height = bounds.Bottom - bounds.Top;
        }
        if (!allowMinimized && (width < 80 || height < 80)) return null;
        return new WindowInfo(hwnd, title.ToString(), processName, width, height);
    }

    public IWindowCaptureSession Start(WindowInfo window) => new GraphicsWindowSession(window);

    private static class Native
    {
        public delegate bool EnumCallback(nint hwnd, nint lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumCallback callback, nint lParam);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(nint hwnd);
        [DllImport("user32.dll")] public static extern nint GetWindow(nint hwnd, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(nint hwnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(nint hwnd, out Rectangle bounds);
        [StructLayout(LayoutKind.Sequential)] public struct Rectangle { public int Left, Top, Right, Bottom; }
    }
}

internal sealed class GraphicsWindowSession : IWindowCaptureSession
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess { nint GetInterface(in Guid iid); }

    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
    private static readonly Guid ItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid TextureGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private readonly Device _device;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _frameGate = new();
    private Texture2D? _staging;
    private SizeInt32 _size;
    private bool _disposed;
    private int _targetFps = 60;
    private long _nextFrameTicks;
    private long _lastTransientLog;
    private int _consecutiveFrameFailures;
    public int TargetFps
    {
        get => Volatile.Read(ref _targetFps);
        set => Volatile.Write(ref _targetFps, Math.Clamp(value, 1, 120));
    }
    public event EventHandler<Display.DisplayFrame>? FrameArrived;
    public event EventHandler<Exception>? CaptureFailed;

    public GraphicsWindowSession(WindowInfo window)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var pointer = interop.CreateForWindow(window.Handle, ItemGuid);
        try { _item = GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
        _size = _item.Size;
        _device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        using var dxgi = _device.QueryInterface<SharpDX.DXGI.Device3>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var winrtPointer));
        try { _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(winrtPointer); }
        finally { Marshal.Release(winrtPointer); }
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _session = _pool.CreateCaptureSession(_item);
        _item.Closed += (_, _) => CaptureFailed?.Invoke(this, new InvalidOperationException("Window closed."));
        _pool.FrameArrived += OnFrame;
        _session.StartCapture();
    }

    private void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_frameGate)
        {
            if (_disposed) return;
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                // Window Graphics Capture is event driven and may deliver frames faster
                // than the compositor can use them (for example, a high-refresh browser).
                // Drain every frame from the pool, but only perform the synchronous GPU
                // readback at the app's configured capture rate.
                var now = Stopwatch.GetTimestamp();
                var interval = Stopwatch.Frequency / Math.Max(1, TargetFps);
                while (true)
                {
                    var next = Interlocked.Read(ref _nextFrameTicks);
                    if (now < next) return;
                    if (Interlocked.CompareExchange(ref _nextFrameTicks, now + interval, next) == next) break;
                }
                var size = frame.ContentSize;
                if (size.Width <= 0 || size.Height <= 0) return;
                if (size.Width != _size.Width || size.Height != _size.Height)
                {
                    // A resize frame still owns a surface from the old pool. Release
                    // it before recreating, then wait for a surface of the new size.
                    frame.Dispose();
                    _size = size;
                    _staging?.Dispose(); _staging = null;
                    sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                    return;
                }
                var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
                var texturePointer = access.GetInterface(TextureGuid);
                using var texture = new Texture2D(texturePointer);
                var description = texture.Description;
                if (description.Width < size.Width || description.Height < size.Height) return;
                if (_staging is not null && (_staging.Description.Width != description.Width || _staging.Description.Height != description.Height))
                { _staging.Dispose(); _staging = null; }
                _staging ??= new Texture2D(_device, new Texture2DDescription
                {
                    Width = description.Width, Height = description.Height, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging, CpuAccessFlags = CpuAccessFlags.Read
                });
                _device.ImmediateContext.CopyResource(texture, _staging);
                var mapped = _device.ImmediateContext.MapSubresource(_staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    var stride = size.Width * 4;
                    var pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(stride * size.Height);
                    for (var y = 0; y < size.Height; y++)
                        Marshal.Copy(nint.Add(mapped.DataPointer, y * mapped.RowPitch), pixels, y * stride, stride);
                    var result = new Display.DisplayFrame(size.Width, size.Height, stride, pixels);
                    if (FrameArrived is { } consumer) consumer.Invoke(this, result); else result.Dispose();
                }
                finally { _device.ImmediateContext.UnmapSubresource(_staging, 0); }
                _consecutiveFrameFailures = 0;
            }
            catch (SharpDXException ex) when (!_disposed && ex.ResultCode != SharpDX.DXGI.ResultCode.DeviceRemoved && ex.ResultCode != SharpDX.DXGI.ResultCode.DeviceReset)
            {
                // Transient resize/readback failures must not tear down a healthy
                // WGC session or remove its capture border while a window is covered.
                var now = Stopwatch.GetTimestamp();
                if (now - _lastTransientLog > Stopwatch.Frequency * 5)
                { _lastTransientLog = now; AravalsStream.Core.Services.AppLog.Write("Capture", $"Window frame skipped: {ex.Message}"); }
                if (++_consecutiveFrameFailures >= 20) CaptureFailed?.Invoke(this, ex);
            }
            catch (Exception ex) when (!_disposed) { CaptureFailed?.Invoke(this, ex); }
        }
    }

    public void Dispose()
    {
        lock (_frameGate)
        {
            if (_disposed) return;
            _disposed = true;
            _pool.FrameArrived -= OnFrame;
            _session.Dispose(); _pool.Dispose(); _staging?.Dispose(); _winrtDevice.Dispose(); _device.Dispose();
        }
    }
}
