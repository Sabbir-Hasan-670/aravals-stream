using AravalsStream.Core.Recording;
using AravalsStream.Core.Recording.Models;
using Xunit;

namespace AravalsStream.Tests;

public sealed class StreamingArgumentTests
{
    private static RecordingSettings Settings(int bitrate, int fps, int audio) => new()
    {
        Video = new VideoEncoderSettings { BitrateKbps = bitrate, FrameRate = fps, KeyframeIntervalSeconds = 2 },
        Audio = new AudioEncoderSettings { BitrateKbps = audio }
    };

    [Fact]
    public void IndependentDestinations_ProduceIndependentEncoderArguments()
    {
        const string fakeUrl = "rtmp://example.invalid/live/test-token";
        var a = FfmpegArgumentBuilder.Build(Settings(8000, 60, 192), "libx264", 1920, 1080, "videoA", "audioA", fakeUrl, true);
        var b = FfmpegArgumentBuilder.Build(Settings(3500, 30, 160), "libx264", 1080, 1920, "videoB", "audioB", fakeUrl, true);
        Assert.Contains("-b:v 8000k", a);
        Assert.Contains("-b:v 3500k", b);
        Assert.Contains("-b:a 192k", a);
        Assert.Contains("-b:a 160k", b);
        Assert.Contains("-r 60", a);
        Assert.Contains("-r 30", b);
        Assert.Contains("-g 120", a);
        Assert.Contains("-g 60", b);
        Assert.Contains("-s 1920x1080", a);
        Assert.Contains("-s 1080x1920", b);
        Assert.Contains("-f flv", a);
        Assert.DoesNotContain("-b:v 3500k", a);
        Assert.DoesNotContain("-b:v 8000k", b);
    }

    [Fact]
    public void LoggingRedaction_RemovesUrlAndKey()
    {
        const string key = "dummy-secret-key";
        const string url = "rtmps://example.invalid/live/dummy-secret-key";
        var raw = "Failed to open " + url + ": " + key;
        var safe = FfmpegArgumentBuilder.Redact(raw, url);
        Assert.DoesNotContain(key, safe);
        Assert.DoesNotContain(url, safe);
        Assert.Contains("redacted RTMP destination", safe);
    }

    [Fact]
    public void SharedEncoderPipeOutput_OverwritesNamedPipeWithoutPrompt()
    {
        var args = FfmpegArgumentBuilder.Build(
            Settings(6000, 60, 160), "h264_nvenc", 1920, 1080,
            "video", "audio", @"\\.\pipe\encoded", streaming: true, inputPixelFormat: "nv12");

        Assert.StartsWith("-y ", args);
        Assert.Contains("-f flv", args);
        Assert.Contains(@"\\.\pipe\encoded", args);
    }
}
