namespace AravalsStream.Core.Settings;

public sealed class AudioSettings
{
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 2;
    public string? MonitoringDeviceId { get; set; }
    public string? MonitoringDeviceName { get; set; }
    public string? DefaultMicrophoneId { get; set; }
    public string? DefaultDesktopAudioId { get; set; }
    public AravalsStream.Core.Audio.AudioFilterSettings? MicrophoneFilters { get; set; }
}

public sealed class AudioRouteSetting
{
    public Guid ChannelId { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string TargetOutputKey { get; set; } = string.Empty; // "Recording" or destination ID
    public bool Enabled { get; set; } = true;
    public float RouteGain { get; set; } = 1.0f; // 0.0 to 2.0
}
