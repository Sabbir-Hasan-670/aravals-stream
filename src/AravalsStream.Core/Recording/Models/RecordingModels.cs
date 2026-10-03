using AravalsStream.Core.Models;

namespace AravalsStream.Core.Recording.Models;

public enum FfmpegStatus
{
    Available,
    Missing,
    Invalid
}

public sealed record FfmpegResolution(
    FfmpegStatus Status,
    string? FfmpegPath,
    string? FfprobePath,
    string? Version,
    string? ErrorMessage);

public sealed record EncoderInfo(
    string Id,
    string DisplayName,
    bool HardwareAccelerated,
    string Vendor,
    bool Available,
    bool Recommended);

public sealed class VideoEncoderSettings
{
    public string EncoderId { get; set; } = "auto";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int FrameRate { get; set; } = 60;
    public int BitrateKbps { get; set; } = 6000;
    public int KeyframeIntervalSeconds { get; set; } = 2;
    public string Preset { get; set; } = "veryfast";
    public string RateControl { get; set; } = "CBR";
}

public sealed class AudioEncoderSettings
{
    public string Codec { get; set; } = "aac";
    public int BitrateKbps { get; set; } = 192;
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 2;
}

public sealed class RecordingSettings
{
    public string OutputDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "Aravals Stream");
    public string Container { get; set; } = "mkv";
    public OutputMode Mode { get; set; } = OutputMode.Horizontal;
    public string? CustomFfmpegPath { get; set; }
    public VideoEncoderSettings Video { get; set; } = new();
    public AudioEncoderSettings Audio { get; set; } = new();
}

public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Paused,
    Stopping,
    Error
}

public enum MediaCadenceState
{
    Healthy,
    SlightlyBehind,
    Degraded,
    Critical
}

public sealed record RecordingTelemetry(
    double CurrentFps,
    long FramesEncoded,
    long FramesDropped,
    TimeSpan ElapsedTime,
    int BitrateKbps,
    string ActiveEncoder)
{
    public double? MeasuredBitrateKbps { get; init; }
    public double VideoPipeWriteMs { get; init; }
    public double AudioPipeWriteMs { get; init; }
    public int VideoQueueDepth { get; init; }
    public int AudioQueueDepth { get; init; }
    public long ScheduledFrames { get; init; }
    public long UniqueFrames { get; init; }
    public long RepeatedFrames { get; init; }
    public long RecordingSchedulerDrops { get; init; }
    public long EncoderBackpressureDrops { get; init; }
    public long EncoderDrops { get; init; }
    public double AverageVideoPipeWriteMs { get; init; }
    public double MaximumVideoPipeWriteMs { get; init; }
    public long VideoPipeDeadlineMisses { get; init; }
    public double RealtimeRatio { get; init; } = 1.0;
    public MediaCadenceState CadenceState { get; init; } = MediaCadenceState.Healthy;
    public TimeSpan MediaDuration { get; init; } = TimeSpan.Zero;
    public double PublishWallDurationSeconds { get; init; }
    public DateTimeOffset? FirstMediaSubmissionUtc { get; init; }
    public DateTimeOffset? LastMediaSubmissionUtc { get; init; }
    public DateTimeOffset? StopRequestedUtc { get; init; }
    public DateTimeOffset? PipesClosedUtc { get; init; }
    public long PostStopFrames { get; init; }
}

