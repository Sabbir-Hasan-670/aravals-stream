namespace AravalsStream.Core.Platforms;

public sealed class PlatformProfile
{
    public string Id { get; init; } = "";
    public PlatformType PlatformType { get; init; }
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string IconData { get; init; } = "";
    public string BrandColor { get; init; } = "#3B82F6";
    public bool SupportsHorizontal { get; init; } = true;
    public bool SupportsVertical { get; init; } = true;
    public bool SupportsMultipleOutputs { get; init; } = true;
    public bool SupportsChat { get; init; } = false;
    public bool SupportsViewerStats { get; init; } = false;
    public bool SupportsFollowers { get; init; } = false;
    public int DefaultVideoBitrateKbps { get; init; } = 6000;
    public int DefaultAudioBitrateKbps { get; init; } = 160;
    public int DefaultFps { get; init; } = 60;
    public int DefaultKeyframeIntervalSeconds { get; init; } = 2;
    public string DefaultProtocol { get; init; } = "RTMP";
    public string DefaultServerUrl { get; init; } = "";
    public string DocumentationUrl { get; init; } = "";
    public int MaxVideoBitrateKbps { get; init; } = 15000;
}
