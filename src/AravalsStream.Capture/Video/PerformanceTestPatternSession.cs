using System.Diagnostics;
using AravalsStream.Capture.Display;

namespace AravalsStream.Capture.Video;

public sealed class PerformanceTestPatternSession : IVideoCaptureSession
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private bool _disposed;

    public event EventHandler<DisplayFrame>? FrameArrived;
    public event EventHandler<Exception>? CaptureFailed;

    public PerformanceTestPatternSession(int width = 1920, int height = 1080, int fps = 60)
    {
        _width = width;
        _height = height;
        _fps = Math.Clamp(fps, 1, 120);
        _worker = Task.Run(() => GenerateLoop(_stop.Token));
    }

    private void GenerateLoop(CancellationToken ct)
    {
        var stride = _width * 4;
        var bytes = checked(stride * _height);
        var intervalTicks = (double)Stopwatch.Frequency / _fps;
        var startTicks = Stopwatch.GetTimestamp();
        long frameIndex = 0;

        int boxW = 240;
        int boxH = 180;
        int posX = 100;
        int posY = 100;
        int velX = 8;
        int velY = 6;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var next = frameIndex + 1;
                var targetTicks = startTicks + (long)(next * intervalTicks);
                var current = Stopwatch.GetTimestamp();
                var remaining = targetTicks - current;
                if (remaining > 0)
                {
                    var delayMs = (int)(remaining * 1000 / Stopwatch.Frequency);
                    if (delayMs > 0) Thread.Sleep(delayMs);
                    while (Stopwatch.GetTimestamp() < targetTicks) { Thread.SpinWait(10); }
                }
                frameIndex = next;

                // Update box position
                posX += velX;
                posY += velY;
                if (posX <= 0) { posX = 0; velX = -velX; }
                else if (posX + boxW >= _width) { posX = _width - boxW; velX = -velX; }
                if (posY <= 0) { posY = 0; velY = -velY; }
                else if (posY + boxH >= _height) { posY = _height - boxH; velY = -velY; }

                var pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(bytes);
                // Background: dark slate gradient
                var bgLuma = (byte)(24 + (frameIndex % 32));
                var span32 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(0, bytes));
                uint bgPixel = 0xFF000000U | ((uint)bgLuma << 16) | ((uint)(bgLuma + 10) << 8) | (uint)(bgLuma + 20);
                span32.Fill(bgPixel);

                // Draw high-contrast moving box (e.g. bright cyan/magenta)
                uint boxColor = ((frameIndex / 30) % 2 == 0) ? 0xFF00E5FFU : 0xFFFF007FU;
                for (var y = posY; y < posY + boxH && y < _height; y++)
                {
                    var rowStart = y * _width + posX;
                    for (var x = 0; x < boxW && posX + x < _width; x++)
                    {
                        span32[rowStart + x] = boxColor;
                    }
                }

                // Draw a moving vertical timing indicator line
                var lineX = (int)((frameIndex * 12) % _width);
                for (var y = 0; y < _height; y++)
                {
                    span32[y * _width + lineX] = 0xFFFFFFFFU;
                }

                var frame = new DisplayFrame(_width, _height, stride, pixels);
                if (FrameArrived is { } consumer) consumer.Invoke(this, frame);
                else frame.Dispose();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CaptureFailed?.Invoke(this, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        try { _worker.Wait(2000); } catch { }
        _stop.Dispose();
    }
}
