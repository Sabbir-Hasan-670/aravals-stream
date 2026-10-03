using AravalsStream.Core.Models;
using AravalsStream.Core.Services;

namespace AravalsStream.Core.Streaming;

/// <summary>
/// Deterministic compatibility key for Encode-Once, Publish-Many architecture.
/// Destinations may share a single hardware encoder session if and only if
/// all video, audio, rate control, and pixel format parameters are strictly identical.
/// </summary>
public sealed record EncoderCompatibilityKey(
    OutputMode CanvasMode,
    int Width,
    int Height,
    int FrameRate,
    string VideoCodec,
    string EncoderBackend,
    int VideoBitrateKbps,
    string RateControl,
    int KeyframeIntervalSeconds,
    string Profile,
    string Preset,
    string PixelFormat,
    string AudioMixFingerprint,
    string AudioCodec,
    int AudioBitrateKbps,
    int SampleRate,
    int Channels)
{
    public static EncoderCompatibilityKey For(
        Destination destination,
        string encoderBackend,
        string audioMixFingerprint,
        string pixelFormat = "nv12",
        string rateControl = "ffmpeg-default",
        string profile = "ffmpeg-default",
        string? preset = null)
    {
        if (string.IsNullOrWhiteSpace(audioMixFingerprint))
            throw new ArgumentException("An explicit audio mix fingerprint is required for encoder sharing.", nameof(audioMixFingerprint));

        var (width, height) = CanvasLayout.Size(destination.OutputMode);
        preset ??= encoderBackend switch
        {
            "libx264" => "veryfast",
            "h264_nvenc" => "p4",
            "h264_qsv" => "medium",
            "h264_amf" => "balanced",
            _ => "encoder-default"
        };
        return new EncoderCompatibilityKey(
            CanvasMode: destination.OutputMode,
            Width: width,
            Height: height,
            FrameRate: destination.FrameRate,
            VideoCodec: "h264",
            EncoderBackend: encoderBackend,
            VideoBitrateKbps: destination.VideoBitrateKbps,
            RateControl: rateControl,
            KeyframeIntervalSeconds: destination.KeyframeIntervalSeconds,
            Profile: profile,
            Preset: preset,
            PixelFormat: pixelFormat,
            AudioMixFingerprint: audioMixFingerprint,
            AudioCodec: "aac",
            AudioBitrateKbps: destination.AudioBitrateKbps,
            SampleRate: 48000,
            Channels: 2);
    }

    public static IReadOnlyList<IReadOnlyList<T>> GroupCompatible<T>(
        IEnumerable<T> items,
        Func<T, EncoderCompatibilityKey> keySelector)
    {
        return items.GroupBy(keySelector)
            .Select(group => (IReadOnlyList<T>)group.ToArray())
            .ToArray();
    }
}

public static class AudioMixFingerprint
{
    public static string For(AravalsStream.Core.Audio.AudioMixer mixer, IEnumerable<AravalsStream.Core.Audio.AudioChannel> channels)
    {
        var activeChannels = channels
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => FormattableString.Invariant($"{c.Name}:{c.Volume:F2}:{(c.Muted ? 1 : 0)}"));

        return string.Join(";", activeChannels);
    }

    public static string For(AravalsStream.Core.Audio.AudioMixer mixer, string outputTargetKey,
        int sampleRate = 48000, int channels = 2)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTargetKey);
        var routes = mixer.Channels
            .OrderBy(c => c.Id)
            .Select(c => FormattableString.Invariant(
                $"{c.Id:N}:{(int)c.MonitoringMode}:{c.SyncOffsetMs}:{c.Volume:R}:{(c.Muted ? 1 : 0)}:{(mixer.Matrix.IsRouteEnabled(c.Id, outputTargetKey) ? 1 : 0)}:{mixer.Matrix.GetRouteGain(c.Id, outputTargetKey):R}"));
        return FormattableString.Invariant($"{sampleRate}/{channels}|") + string.Join(";", routes);
    }
}
