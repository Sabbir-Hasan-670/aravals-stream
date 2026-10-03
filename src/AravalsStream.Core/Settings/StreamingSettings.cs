namespace AravalsStream.Core.Settings;

public sealed class StreamingSettings
{
    public string DefaultEncoder { get; set; } = "auto";
    public int DefaultKeyframeIntervalSeconds { get; set; } = 2;
    public int DefaultAudioBitrateKbps { get; set; } = 160;
    public bool AutoReconnect { get; set; } = true;
    public bool AdaptiveBitrateDefault { get; set; } = false;
    public bool AutoRecoverBitrateDefault { get; set; } = false;
    public int BandwidthWarningThresholdMbps { get; set; } = 10;
}
