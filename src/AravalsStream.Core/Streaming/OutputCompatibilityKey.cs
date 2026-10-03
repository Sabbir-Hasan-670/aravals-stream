using AravalsStream.Core.Models;
using AravalsStream.Core.Services;

namespace AravalsStream.Core.Streaming;

// An encoded stream can be shared only when every encoded characteristic and
// the actual audio mix are identical. The current media runtime still creates
// independent encoders because it has no independent packet publisher yet.
public sealed record OutputCompatibilityKey(
    OutputMode Canvas, int Width, int Height, int FrameRate,
    string VideoCodec, string EncoderId, int VideoBitrateKbps, int KeyframeIntervalSeconds,
    string PixelFormat, string AudioMixId, string AudioCodec, int AudioBitrateKbps,
    int AudioSampleRate, int AudioChannels)
{
    public static OutputCompatibilityKey For(Destination destination, string resolvedEncoder,
        string audioMixId, string pixelFormat = "yuv420p")
    {
        if (string.IsNullOrWhiteSpace(audioMixId))
            throw new ArgumentException("An explicit audio mix identity is required for encoder sharing.", nameof(audioMixId));
        var (width, height) = CanvasLayout.Size(destination.OutputMode);
        return new OutputCompatibilityKey(destination.OutputMode, width, height,
            destination.FrameRate, "h264", resolvedEncoder, destination.VideoBitrateKbps,
            destination.KeyframeIntervalSeconds, pixelFormat, audioMixId, "aac",
            destination.AudioBitrateKbps, 48000, 2);
    }

    public static IReadOnlyList<IReadOnlyList<T>> CompatibleGroups<T>(IEnumerable<T> outputs,
        Func<T, OutputCompatibilityKey> keySelector) => outputs.GroupBy(keySelector)
            .Select(group => (IReadOnlyList<T>)group.ToArray()).ToArray();
}
