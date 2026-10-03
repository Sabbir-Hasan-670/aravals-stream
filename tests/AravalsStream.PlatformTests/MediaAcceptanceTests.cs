using System.Diagnostics;
using System.Text.Json;
using AravalsStream.Platform;
using Xunit;
using AravalsStream.Core.Models;

namespace AravalsStream.PlatformTests;
public sealed class MediaAcceptanceTests
{
    [Fact]
    public async Task ActualPreviewContainsJpegFramesAndStopsCleanly()
    {
        var ffmpeg = FindFfmpeg();
        var plan = new MediaPlan(PlatformCapture.Current, new(CaptureKind.TestVideo, ""), [], new(640, 360));
        await using var process = new FfmpegProcess(ffmpeg, MediaArguments.Preview(plan));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var bytes = new byte[65536]; var count = await process.Video.ReadAsync(bytes, deadline.Token);
        Assert.True(count > 100); Assert.Equal(0xff, bytes[0]); Assert.Equal(0xd8, bytes[1]);
    }
    [Fact]
    public async Task ActualRecordingContainsEncodedMovingVideoAndAudio()
    {
        var ffmpeg = FindFfmpeg();
        var path = Path.Combine(Path.GetTempPath(), "aravals-port-" + Guid.NewGuid().ToString("N") + ".mkv");
        try
        {
            var plan = new MediaPlan(PlatformCapture.Current, new(CaptureKind.TestVideo, ""), [new(CaptureKind.TestAudio, "")], new(640, 360));
            await using (var process = new FfmpegProcess(ffmpeg, MediaArguments.Output(plan, path, true)))
            { await Task.Delay(TimeSpan.FromSeconds(3)); Assert.False(process.HasExited); }
            var probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            var start = new ProcessStartInfo(probe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", path }) start.ArgumentList.Add(arg);
            using var p = Process.Start(start)!;
            var result = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync(); Assert.Equal(0, p.ExitCode);
            using var json = JsonDocument.Parse(result);
            var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Assert.Contains(streams, s => s.GetProperty("codec_type").GetString() == "video" && s.GetProperty("codec_name").GetString() == "h264");
            Assert.Contains(streams, s => s.GetProperty("codec_type").GetString() == "audio" && s.GetProperty("codec_name").GetString() == "aac");
            Assert.True(double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture) > 1);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task ActualSceneCompositorOutputsAFrameWithRotationAndOpacity()
    {
        var plan = new MediaPlan(PlatformCapture.Current, new(CaptureKind.TestVideo, ""), [], new(640, 360))
        {
            Layers = [new(new(CaptureKind.TestVideo, ""), new SourceTransform { Width = 320, Height = 180, X = 70, Y = 40, Opacity = 0.7, Rotation = 20 })]
        };
        await using var process = new FfmpegProcess(FindFfmpeg(), MediaArguments.Preview(plan));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var bytes = new byte[65536]; var count = await process.Video.ReadAsync(bytes, deadline.Token);
        Assert.True(count > 100); Assert.Equal(0xff, bytes[0]); Assert.Equal(0xd8, bytes[1]);
    }
    private static string FindFfmpeg()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "ffmpeg", "bin", "ffmpeg.exe");
            if (OperatingSystem.IsWindows() && File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return FfmpegProcess.FindExecutable();
    }
}
