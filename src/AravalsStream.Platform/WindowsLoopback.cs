using System.IO.Pipes;
using System.Runtime.Versioning;
using AravalsStream.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AravalsStream.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsLoopback : IAsyncDisposable
{
    public const string DefaultDevice = "system-default-output";
    private readonly MMDevice _device;
    private readonly WasapiLoopbackCapture _capture;
    private readonly AudioFormatConverter _converter;
    private readonly BufferedWaveProvider _buffer = new(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
    { BufferDuration = TimeSpan.FromMilliseconds(250), DiscardOnBufferOverflow = true, ReadFully = true };
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writer;
    private int _disposed;
    public string PipePath { get; }
    public string? Error { get; private set; }

    public static IReadOnlyList<CaptureDevice> Enumerate()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<CaptureDevice> { new(CaptureKind.DesktopAudio, DefaultDevice, "System default speakers") };
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device) result.Add(new(CaptureKind.DesktopAudio, device.ID, device.FriendlyName));
        return result;
    }

    public WindowsLoopback(string identifier)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = identifier == DefaultDevice ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(identifier);
        try
        {
            _capture = new WasapiLoopbackCapture(_device);
            var format = _capture.WaveFormat;
            var sampleType = format.Encoding == WaveFormatEncoding.IeeeFloat ? RawSampleType.Float32 : format.BitsPerSample switch
            { 16 => RawSampleType.Pcm16, 24 => RawSampleType.Pcm24, 32 => RawSampleType.Pcm32, _ => throw new IOException("Unsupported speaker audio format.") };
            _converter = new(new(format.SampleRate, format.Channels, sampleType));
            var name = "AravalsStream-audio-" + Guid.NewGuid().ToString("N");
            PipePath = @"\\.\pipe\" + name;
            _pipe = new(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
            // Start capture only after FFmpeg connects. No background audio is collected for idle outputs.
            _writer = WriteAudio();
        }
        catch { _capture?.Dispose(); _device.Dispose(); throw; }
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        if (_stop.IsCancellationRequested) return;
        try
        {
            var samples = _converter.Convert(args.Buffer.AsSpan(0, args.BytesRecorded));
            var bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            _buffer.AddSamples(bytes, 0, bytes.Length);
        }
        catch { Error = "Speaker audio capture failed. Stop the output and refresh audio devices."; _stop.Cancel(); }
    }
    private void OnStopped(object? sender, StoppedEventArgs args)
    { if (!_stop.IsCancellationRequested) { Error = "Speaker audio device stopped. Stop the output and refresh audio devices."; _stop.Cancel(); } }

    private async Task WriteAudio()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_stop.Token);
            _capture.StartRecording();
            var bytes = new byte[480 * 2 * sizeof(float)];
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                // Fixed cadence emits silence when WASAPI receives no packets, keeping video moving.
                _buffer.Read(bytes, 0, bytes.Length);
                await _pipe.WriteAsync(bytes, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { if (!_stop.IsCancellationRequested) Error = "Speaker audio transport stopped. Check the output and refresh audio devices."; }
        finally { try { if (_pipe.IsConnected) _pipe.Disconnect(); } catch (IOException) { } }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _capture.DataAvailable -= OnData; _capture.RecordingStopped -= OnStopped;
        try { await Task.Run(_capture.Dispose); await _writer; }
        finally { _pipe.Dispose(); _device.Dispose(); _stop.Dispose(); }
    }
}
