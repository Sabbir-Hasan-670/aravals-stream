using System.Runtime.InteropServices;
using System.Windows.Forms;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace AravalsStream.Capture.Display;

public sealed class DesktopDuplicationCaptureService : IDisplayCaptureService
{
    private readonly bool _reportTimings;
    public DesktopDuplicationCaptureService(bool reportTimings = false) => _reportTimings = reportTimings;

    public IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
        var displays = new List<DisplayInfo>();
        using var factory = new Factory1();
        var adapterIndex = 0;
        foreach (var adapter in factory.Adapters1)
        {
            using (adapter)
            {
                var outputIndex = 0;
                foreach (var output in adapter.Outputs)
                {
                    using (output)
                    {
                        var description = output.Description;
                        if (description.IsAttachedToDesktop)
                        {
                            var bounds = description.DesktopBounds;
                            var primary = Screen.AllScreens.Any(s => s.DeviceName.Equals(description.DeviceName, StringComparison.OrdinalIgnoreCase) && s.Primary);
                            displays.Add(new DisplayInfo(description.DeviceName, description.DeviceName, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, primary, adapterIndex, outputIndex));
                        }
                    }
                    outputIndex++;
                }
            }
            adapterIndex++;
        }
        return displays;
    }

    public IDisplayCaptureSession Start(DisplayInfo display) => new DesktopDuplicationSession(display, _reportTimings);
    public void Dispose() { }
}

internal sealed class DesktopDuplicationSession : IDisplayCaptureSession
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly bool _reportTimings;
    private bool _disposed;
    public DisplayInfo Display { get; }
    public int TargetFps { get; set; } = 60;
    public event EventHandler<DisplayFrame>? FrameArrived;
    public event EventHandler<Exception>? CaptureFailed;

    public DesktopDuplicationSession(DisplayInfo display, bool reportTimings = false)
    {
        Display = display;
        _reportTimings = reportTimings;
        _worker = Task.Run(() => CaptureLoop(_stop.Token));
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        try
        {
            using var factory = new Factory1();
            using var adapter = factory.GetAdapter1(Display.AdapterIndex);
            using var output = adapter.GetOutput(Display.OutputIndex);
            using var output1 = output.QueryInterface<Output1>();
            using var device = new Device(adapter, DeviceCreationFlags.BgraSupport);
            using var duplication = output1.DuplicateOutput(device);
            using var staging = new Texture2D(device, new Texture2DDescription
            {
                Width = Display.Width,
                Height = Display.Height,
                ArraySize = 1,
                MipLevels = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                OptionFlags = ResourceOptionFlags.None
            });

            var nextFrameTicks = 0L;
            var hasStagedFrame = false;
            using var frameWait = HighResolutionFrameWait.TryCreate();
            var timingStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long timingFrames = 0, duplicateFrames = 0, acquireTicks = 0, gpuCopyTicks = 0, mapTicks = 0, cpuCopyTicks = 0, captureCallbackTicks = 0, totalFrameTicks = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var targetFps = TargetFps;
                if (targetFps > 0)
                {
                    var intervalTicks = System.Diagnostics.Stopwatch.Frequency / targetFps;
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (nextFrameTicks == 0) nextFrameTicks = now;
                    var remaining = nextFrameTicks - now;
                    if (remaining > 0)
                    {
                        if (frameWait is not null) frameWait.WaitUntil(nextFrameTicks);
                        else
                        {
                            var delayMs = (int)(remaining * 1000 / System.Diagnostics.Stopwatch.Frequency);
                            if (delayMs > 0) Thread.Sleep(Math.Min(100, delayMs));
                            while ((remaining = nextFrameTicks - System.Diagnostics.Stopwatch.GetTimestamp()) > 0)
                                Thread.SpinWait(32);
                        }
                    }
                }
                var frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (targetFps > 0) nextFrameTicks = frameStart + System.Diagnostics.Stopwatch.Frequency / targetFps;

                SharpDX.DXGI.Resource? resource = null;
                var acquired = false;
                int missedFrames = 0;
                try
                {
                    var acquireStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    var result = duplication.TryAcquireNextFrame(0, out var frameInfo, out resource);
                    acquireTicks += System.Diagnostics.Stopwatch.GetTimestamp() - acquireStart;
                    if (result == SharpDX.DXGI.ResultCode.WaitTimeout)
                    {
                        if (!hasStagedFrame) continue;
                        duplicateFrames++;
                    }
                    else
                    {
                        result.CheckError();
                        missedFrames = frameInfo.AccumulatedFrames > 1 ? frameInfo.AccumulatedFrames - 1 : 0;
                        acquired = true;
                        using var texture = resource.QueryInterface<Texture2D>();
                        var gpuCopyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        device.ImmediateContext.CopyResource(texture, staging);
                        gpuCopyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - gpuCopyStart;
                        hasStagedFrame = true;
                    }
                }
                catch (SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.WaitTimeout)
                {
                    continue;
                }
                finally
                {
                    resource?.Dispose();
                    if (acquired) duplication.ReleaseFrame();
                }

                if (!hasStagedFrame) continue;

                var mapStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var mapped = device.ImmediateContext.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                mapTicks += System.Diagnostics.Stopwatch.GetTimestamp() - mapStart;
                try
                {
                    var stride = Display.Width * 4;
                    var pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(stride * Display.Height);
                    var copyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (mapped.RowPitch == stride)
                        Marshal.Copy(mapped.DataPointer, pixels, 0, checked(stride * Display.Height));
                    else
                        for (var y = 0; y < Display.Height; y++)
                            Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * mapped.RowPitch), pixels, y * stride, stride);
                    cpuCopyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - copyStart;
                    var frame = new DisplayFrame(Display.Width, Display.Height, stride, pixels, missedFrames);
                    var callbackStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (FrameArrived is { } consumer) consumer.Invoke(this, frame);
                    else frame.Dispose();
                    captureCallbackTicks += System.Diagnostics.Stopwatch.GetTimestamp() - callbackStart;
                    totalFrameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - frameStart;
                    timingFrames++;
                }
                finally
                {
                    device.ImmediateContext.UnmapSubresource(staging, 0);
                }

                if (_reportTimings)
                {
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    var elapsedTicks = now - timingStart;
                    if (elapsedTicks >= System.Diagnostics.Stopwatch.Frequency * 5)
                    {
                        var seconds = elapsedTicks / (double)System.Diagnostics.Stopwatch.Frequency;
                        static double Ms(long ticks, long count) => count == 0 ? 0 : ticks * 1000d / System.Diagnostics.Stopwatch.Frequency / count;
                        AravalsStream.Core.Services.AppLog.Write("RemoteMedia", $"DesktopCaptureStages at {now}, intervalSeconds={seconds:F2}, captureFps={timingFrames / seconds:F2}, duplicateFrames={duplicateFrames}, acquireAvgMs={Ms(acquireTicks, timingFrames):F2}, gpuCopyAvgMs={Ms(gpuCopyTicks, timingFrames):F2}, mapAvgMs={Ms(mapTicks, timingFrames):F2}, cpuCopyAvgMs={Ms(cpuCopyTicks, timingFrames):F2}, callbackAvgMs={Ms(captureCallbackTicks, timingFrames):F2}, totalFrameWorkAvgMs={Ms(totalFrameTicks, timingFrames):F2}");
                        timingStart = now; timingFrames = 0; duplicateFrames = 0; acquireTicks = 0; gpuCopyTicks = 0; mapTicks = 0; cpuCopyTicks = 0; captureCallbackTicks = 0; totalFrameTicks = 0;
                    }
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            CaptureFailed?.Invoke(this, ex);
        }
    }

    public void Stop() => _stop.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (Task.CurrentId != _worker.Id)
        {
            try { _worker.Wait(); }
            catch (AggregateException) { }
        }
        _stop.Dispose();
    }
}
