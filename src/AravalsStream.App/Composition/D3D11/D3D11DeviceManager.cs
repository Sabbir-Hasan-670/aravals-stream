using System.Diagnostics;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using AravalsStream.Core.Services;
using Device = SharpDX.Direct3D11.Device;

namespace AravalsStream.App.Composition.D3D11;

/// <summary>
/// Centralized Direct3D 11 device and resource manager.
/// Owns the primary D3D11 device, immediate context, adapter selection,
/// texture pooling, and device lost recovery.
/// </summary>
public sealed class D3D11DeviceManager : IDisposable
{
    private static readonly Lazy<D3D11DeviceManager> _instance = new(() => new D3D11DeviceManager());
    public static D3D11DeviceManager Instance => _instance.Value;

    private readonly object _syncLock = new();
    private Device? _device;
    private DeviceContext? _context;
    private Factory1? _factory;
    private Adapter1? _selectedAdapter;
    private int _deviceGeneration;
    private bool _disposed;

    // Bounded texture pools for reusable render targets and staging textures
    private readonly Dictionary<(int Width, int Height, Format Format, BindFlags BindFlags, ResourceUsage Usage, CpuAccessFlags CpuFlags), Queue<Texture2D>> _texturePool = [];
    private const int MaxPoolSizePerKey = 6;

    public bool IsInitialized
    {
        get
        {
            lock (_syncLock) return _device is not null && !_device.IsDisposed;
        }
    }

    public int DeviceGeneration
    {
        get
        {
            lock (_syncLock) return _deviceGeneration;
        }
    }

    public string SelectedAdapterName { get; private set; } = "None";
    public bool IsHardwareDevice { get; private set; }
    public long DedicatedVideoMemoryBytes { get; private set; }
    public long SharedSystemMemoryBytes { get; private set; }

    public event Action? DeviceReset;
    public event Action<string>? DeviceError;

    public D3D11DeviceManager()
    {
        InitializeDevice();
    }

    public bool InitializeDevice()
    {
        lock (_syncLock)
        {
            CleanupCurrentDevice();
            try
            {
                _factory = new Factory1();
                var adapters = _factory.Adapters1;
                if (adapters.Length == 0)
                {
                    DeviceError?.Invoke("No DXGI adapters found on system.");
                    return false;
                }

                // Prefer dedicated GPU (NVIDIA/AMD with highest dedicated video memory), fallback to Intel / Basic
                Adapter1 bestAdapter = adapters[0];
                long maxDedicated = bestAdapter.Description1.DedicatedVideoMemory;

                foreach (var adapter in adapters)
                {
                    var desc = adapter.Description1;
                    if ((desc.Flags & AdapterFlags.Software) != 0) continue;

                    // Choose discrete GPU if dedicated memory is higher
                    if (desc.DedicatedVideoMemory > maxDedicated)
                    {
                        maxDedicated = desc.DedicatedVideoMemory;
                        bestAdapter = adapter;
                    }
                }

                _selectedAdapter = bestAdapter;
                var adapterDesc = _selectedAdapter.Description1;
                SelectedAdapterName = adapterDesc.Description.TrimEnd('\0');
                DedicatedVideoMemoryBytes = adapterDesc.DedicatedVideoMemory;
                SharedSystemMemoryBytes = adapterDesc.SharedSystemMemory;

                // Create Direct3D 11 device with BGRA support for Direct2D / DXGI interoperability
                var creationFlags = DeviceCreationFlags.BgraSupport;
#if DEBUG
                // In debug mode, D3D11 debug layer can be enabled if available
#endif
                FeatureLevel[] featureLevels =
                [
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0
                ];

                try
                {
                    _device = new Device(_selectedAdapter, creationFlags, featureLevels);
                    IsHardwareDevice = true;
                }
                catch (SharpDXException)
                {
                    // Fallback to WARP software driver if hardware creation fails
                    _device = new Device(DriverType.Warp, creationFlags, featureLevels);
                    IsHardwareDevice = false;
                    SelectedAdapterName = "D3D11 WARP Software Rasterizer";
                }

                _context = _device.ImmediateContext;
                _deviceGeneration++;
                return true;
            }
            catch (Exception ex)
            {
                DeviceError?.Invoke($"D3D11 Device initialization failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Executes an action with the D3D11 immediate device context under device lock.
    /// </summary>
    public void WithContext(Action<Device, DeviceContext> action)
    {
        lock (_syncLock)
        {
            if (_device is null || _context is null || _device.IsDisposed)
            {
                throw new InvalidOperationException("D3D11 Device is not initialized or has been disposed.");
            }

            try
            {
                action(_device, _context);
            }
            catch (SharpDXException ex) when (IsDeviceLost(ex))
            {
                HandleDeviceLost(ex);
                throw;
            }
        }
    }

    public T WithContext<T>(Func<Device, DeviceContext, T> func)
    {
        lock (_syncLock)
        {
            if (_device is null || _context is null || _device.IsDisposed)
            {
                throw new InvalidOperationException("D3D11 Device is not initialized or has been disposed.");
            }

            try
            {
                return func(_device, _context);
            }
            catch (SharpDXException ex) when (IsDeviceLost(ex))
            {
                HandleDeviceLost(ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Rent a Texture2D from the pool or create a new one if none available.
    /// </summary>
    public Texture2D RentTexture(int width, int height, Format format, BindFlags bindFlags,
        ResourceUsage usage = ResourceUsage.Default, CpuAccessFlags cpuFlags = CpuAccessFlags.None)
    {
        lock (_syncLock)
        {
            if (_device is null || _device.IsDisposed)
            {
                throw new InvalidOperationException("Cannot rent texture: D3D11 Device is not initialized.");
            }

            var key = (width, height, format, bindFlags, usage, cpuFlags);
            if (_texturePool.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                var tex = queue.Dequeue();
                if (!tex.IsDisposed) return tex;
            }

            var desc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = usage,
                BindFlags = bindFlags,
                CpuAccessFlags = cpuFlags,
                OptionFlags = ResourceOptionFlags.None
            };

            return new Texture2D(_device, desc);
        }
    }

    /// <summary>
    /// Return a rented texture to the pool for reuse.
    /// </summary>
    public void ReturnTexture(Texture2D texture, int? expectedDeviceGeneration = null)
    {
        if (texture.IsDisposed) return;

        lock (_syncLock)
        {
            if (expectedDeviceGeneration is { } expected && expected != _deviceGeneration)
            {
                texture.Dispose();
                return;
            }

            if (_device is null || _device.IsDisposed)
            {
                texture.Dispose();
                return;
            }

            var desc = texture.Description;
            var key = (desc.Width, desc.Height, desc.Format, desc.BindFlags, desc.Usage, desc.CpuAccessFlags);
            if (!_texturePool.TryGetValue(key, out var queue))
            {
                queue = new Queue<Texture2D>();
                _texturePool[key] = queue;
            }

            if (queue.Count < MaxPoolSizePerKey)
            {
                queue.Enqueue(texture);
            }
            else
            {
                texture.Dispose();
            }
        }
    }

    private static bool IsDeviceLost(SharpDXException ex) =>
        ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceRemoved ||
        ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceReset ||
        ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceHung;

    private void HandleDeviceLost(SharpDXException ex)
    {
        AppLog.Write("D3D11", $"Device lost detected: {ex.ResultCode.Code} (0x{ex.ResultCode.Code:X8}): {ex.Message}");
        DeviceError?.Invoke($"Device Lost: {ex.ResultCode.Code}");

        CleanupCurrentDevice();
        if (InitializeDevice())
        {
            DeviceReset?.Invoke();
        }
    }

    private void CleanupCurrentDevice()
    {
        foreach (var queue in _texturePool.Values)
        {
            while (queue.Count > 0)
            {
                var tex = queue.Dequeue();
                try { tex.Dispose(); } catch { }
            }
        }
        _texturePool.Clear();

        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        try { _selectedAdapter?.Dispose(); } catch { }
        try { _factory?.Dispose(); } catch { }

        _context = null;
        _device = null;
        _selectedAdapter = null;
        _factory = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_syncLock)
        {
            CleanupCurrentDevice();
        }
    }
}
