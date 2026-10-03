using NAudio.CoreAudioApi;
using NAudio.Wave;
using AravalsStream.Core.Audio;

namespace AravalsStream.Capture.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsInput, bool IsDefault)
{
    public string Label => $"{Name}{(IsDefault ? "  ·  Default" : "")}";
}

public interface IAudioCaptureService
{
    IReadOnlyList<AudioDeviceInfo> Enumerate(bool input);
    IAudioCaptureSession Start(AudioDeviceInfo device);
}

public interface IAudioCaptureSession : IDisposable
{
    event Action<float[]>? SamplesArrived;
    event Action<Exception>? CaptureFailed;
    RawAudioFormat InputFormat { get; }
}

public sealed class WasapiAudioCaptureService : IAudioCaptureService
{
    public const string DefaultInputId = "system-default-input";
    public const string DefaultOutputId = "system-default-output";
    public IReadOnlyList<AudioDeviceInfo> Enumerate(bool input)
    {
        using var enumerator = new MMDeviceEnumerator();
        var flow = input ? DataFlow.Capture : DataFlow.Render;
        string? defaultId = null;
        try
        {
            using var current = enumerator.GetDefaultAudioEndpoint(flow, input ? Role.Communications : Role.Multimedia);
            defaultId = current.ID;
        }
        catch { }
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, input, d.ID == defaultId)).ToList();
        devices.Insert(0, new AudioDeviceInfo(input ? DefaultInputId : DefaultOutputId,
            input ? "System default microphone" : "System default speakers", input, true));
        return devices;
    }

    public IAudioCaptureSession Start(AudioDeviceInfo device) => new WasapiAudioSession(device);
}

internal sealed class WasapiAudioSession : IAudioCaptureSession
{
    private readonly MMDevice _device;
    private readonly IWaveIn _capture;
    private readonly AudioFormatConverter _converter;
    private bool _disposed;
    public RawAudioFormat InputFormat { get; }
    public event Action<float[]>? SamplesArrived;
    public event Action<Exception>? CaptureFailed;

    public WasapiAudioSession(AudioDeviceInfo info)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = info.Id == WasapiAudioCaptureService.DefaultInputId || info.Id == WasapiAudioCaptureService.DefaultOutputId
            ? enumerator.GetDefaultAudioEndpoint(info.IsInput ? DataFlow.Capture : DataFlow.Render,
                info.IsInput ? Role.Communications : Role.Multimedia)
            : enumerator.GetDevice(info.Id);
        _capture = info.IsInput ? new WasapiCapture(_device) : new WasapiLoopbackCapture(_device);
        var format = _capture.WaveFormat;
        var type = ResolveSampleType(format);
        InputFormat = new RawAudioFormat(format.SampleRate, format.Channels, type);
        _converter = new AudioFormatConverter(InputFormat);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += OnStopped;
        _capture.StartRecording();
    }

    private static RawSampleType ResolveSampleType(WaveFormat format)
    {
        var floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible extended && extended.SubFormat == new Guid("00000003-0000-0010-8000-00AA00389B71");
        if (floating && format.BitsPerSample == 32) return RawSampleType.Float32;
        return format.BitsPerSample switch
        {
            16 => RawSampleType.Pcm16,
            24 => RawSampleType.Pcm24,
            32 => RawSampleType.Pcm32,
            _ => throw new NotSupportedException($"Unsupported WASAPI sample format: {format}")
        };
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_disposed) return;
        try
        {
            var samples = _converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
            if (samples.Length > 0) SamplesArrived?.Invoke(samples);
        }
        catch (Exception ex) { CaptureFailed?.Invoke(ex); }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!_disposed) CaptureFailed?.Invoke(e.Exception ?? new InvalidOperationException("Audio device stopped."));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capture.DataAvailable -= OnData;
        _capture.RecordingStopped -= OnStopped;
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
        _device.Dispose();
    }
}
