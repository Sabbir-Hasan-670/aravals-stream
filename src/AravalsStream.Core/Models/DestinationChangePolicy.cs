namespace AravalsStream.Core.Models;

public static class DestinationChangePolicy
{
    public static bool RequiresRestart(Destination before, Destination after) =>
        before.StreamUrl != after.StreamUrl || before.Protocol != after.Protocol ||
        before.StreamKeyReference != after.StreamKeyReference || before.OutputMode != after.OutputMode ||
        before.EncoderId != after.EncoderId || before.VideoBitrateKbps != after.VideoBitrateKbps ||
        before.AudioBitrateKbps != after.AudioBitrateKbps || before.FrameRate != after.FrameRate ||
        before.KeyframeIntervalSeconds != after.KeyframeIntervalSeconds;
}
