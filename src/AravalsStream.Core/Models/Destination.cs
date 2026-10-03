using System.Text.Json.Serialization;

namespace AravalsStream.Core.Models;

public sealed class Destination
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Platform { get; set; } = "Custom RTMP";
    public string Name { get; set; } = "Custom RTMP";
    public bool Enabled { get; set; } = true;
    public string Protocol { get; set; } = "RTMP";
    public OutputMode OutputMode { get; set; } = OutputMode.Horizontal;
    public string StreamUrl { get; set; } = "";
    public string? StreamKeyReference { get; set; }
    public string EncoderId { get; set; } = "auto";
    public int VideoBitrateKbps { get; set; } = 6000;
    public int AudioBitrateKbps { get; set; } = 160;
    public int FrameRate { get; set; } = 60;
    public int KeyframeIntervalSeconds { get; set; } = 2;
    public bool AutoReconnect { get; set; } = true;
    [JsonIgnore] public DestinationStatus Status { get; set; } = DestinationStatus.Offline;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Destination Copy() => new()
    {
        Id = Id, Platform = Platform, Name = Name, Enabled = Enabled, Protocol = Protocol,
        OutputMode = OutputMode, StreamUrl = StreamUrl, StreamKeyReference = StreamKeyReference,
        EncoderId = EncoderId, VideoBitrateKbps = VideoBitrateKbps, AudioBitrateKbps = AudioBitrateKbps,
        FrameRate = FrameRate, KeyframeIntervalSeconds = KeyframeIntervalSeconds,
        AutoReconnect = AutoReconnect, CreatedAt = CreatedAt, UpdatedAt = UpdatedAt
    };
    public Destination Duplicate()
    {
        var copy = Copy();
        copy.Id = Guid.NewGuid();
        copy.Name += " Copy";
        copy.StreamKeyReference = null;
        copy.Enabled = false;
        copy.Status = DestinationStatus.Disabled;
        copy.CreatedAt = DateTimeOffset.UtcNow;
        copy.UpdatedAt = copy.CreatedAt;
        return copy;
    }
}

