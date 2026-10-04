namespace AravalsStream.Core.Models;

public sealed class CaptureResource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public SourceType Type { get; set; }
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? WindowTitle { get; set; }
    public string? ProcessName { get; set; }
    public int CameraIndex { get; set; }
    public int FormatWidth { get; set; }
    public int FormatHeight { get; set; }
    public int FormatFrameRate { get; set; }
    public string FormatSubtype { get; set; } = "";
    public bool FollowSystemDefault { get; set; }
    public float Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public AravalsStream.Core.Audio.AudioFilterSettings AudioFilters { get; set; } = new();
    public string? RemoteDeviceId { get; set; }
    public string? RemoteHost { get; set; }
    public string? RemoteSecretReference { get; set; }
    public int RemoteLatencyMs { get; set; } = 250;
    public string? RemoteCertificateThumbprint { get; set; }
    public Guid? AssociatedAudioResourceId { get; set; }
}
