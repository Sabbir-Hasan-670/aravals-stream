using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace AravalsStream.App.Composition.D3D11;

/// <summary>
/// Immutable GPU video surface wrapping a Direct3D 11 Texture2D and its views.
/// Returned to the D3D11DeviceManager pool when all consumers release their leases.
/// </summary>
public sealed class GpuVideoFrame : IDisposable
{
    private readonly D3D11DeviceManager _deviceManager;
    private int _referenceCount;
    private bool _disposed;
    private int _ownerReleased;

    public Texture2D Texture { get; }
    public RenderTargetView? RenderTargetView { get; }
    public ShaderResourceView? ShaderResourceView { get; }
    public int Width { get; }
    public int Height { get; }
    public Format Format { get; }
    public long TimestampTicks { get; }
    public long FrameId { get; }
    public int DeviceGeneration { get; }

    internal GpuVideoFrame(
        D3D11DeviceManager deviceManager,
        Texture2D texture,
        RenderTargetView? rtv,
        ShaderResourceView? srv,
        int width,
        int height,
        Format format,
        long timestampTicks,
        long frameId,
        int deviceGeneration)
    {
        _deviceManager = deviceManager;
        Texture = texture;
        RenderTargetView = rtv;
        ShaderResourceView = srv;
        Width = width;
        Height = height;
        Format = format;
        TimestampTicks = timestampTicks;
        FrameId = frameId;
        DeviceGeneration = deviceGeneration;
        _referenceCount = 1;
    }

    /// <summary>
    /// Acquire a new reference-counted lease for a consumer (e.g. YouTube, Twitch, Kick, Recording).
    /// </summary>
    public GpuFrameLease AcquireLease()
    {
        lock (this)
        {
            if (_disposed || Volatile.Read(ref _referenceCount) <= 0)
            {
                throw new ObjectDisposedException(nameof(GpuVideoFrame), "Cannot acquire lease on disposed frame.");
            }
            Interlocked.Increment(ref _referenceCount);
            return new GpuFrameLease(this);
        }
    }

    /// <summary>Transfers the initial owner reference into a lease without incrementing it.</summary>
    internal GpuFrameLease TransferOwnerToLease()
    {
        lock (this)
        {
            if (_disposed || Interlocked.Exchange(ref _ownerReleased, 1) != 0)
                throw new ObjectDisposedException(nameof(GpuVideoFrame));
            return new GpuFrameLease(this);
        }
    }

    internal void Release()
    {
        ReleaseReference();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ownerReleased, 1) != 0) return;
        ReleaseReference();
    }

    private void ReleaseReference()
    {
        if (Interlocked.Decrement(ref _referenceCount) != 0) return;
        lock (this)
        {
            if (Volatile.Read(ref _referenceCount) != 0) return;
            if (_disposed) return;
            _disposed = true;

            try { RenderTargetView?.Dispose(); } catch { }
            try { ShaderResourceView?.Dispose(); } catch { }
            try { _deviceManager.ReturnTexture(Texture, DeviceGeneration); } catch { }
        }
    }
}

/// <summary>
/// Reference-counted lease handle for a GpuVideoFrame.
/// Releasing this lease decrements the parent frame's reference count.
/// </summary>
public sealed class GpuFrameLease : IDisposable
{
    private GpuVideoFrame? _frame;

    public GpuVideoFrame Frame => _frame ?? throw new ObjectDisposedException(nameof(GpuFrameLease));
    public bool IsDisposed => _frame is null;

    internal GpuFrameLease(GpuVideoFrame frame)
    {
        _frame = frame;
    }

    public void Dispose()
    {
        var frame = Interlocked.Exchange(ref _frame, null);
        frame?.Release();
    }
}
