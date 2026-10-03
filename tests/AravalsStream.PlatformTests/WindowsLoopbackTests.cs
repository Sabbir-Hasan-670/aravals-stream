using System.Diagnostics;
using System.Text.Json;
using AravalsStream.Platform;
using Xunit;

namespace AravalsStream.PlatformTests;

public sealed class WindowsLoopbackTests
{
    [Fact]
    public void InternalPcmTransportCannotBeSubstitutedWithAnExternalUrl()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlatformCapture.Arguments(DesktopPlatform.Windows, new(CaptureKind.PcmAudio, "tcp://example.com:1234"), 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlatformCapture.Arguments(DesktopPlatform.MacOS, new(CaptureKind.PcmAudio, @"\\.\pipe\AravalsStream-audio-test"), 30));
        var args = PlatformCapture.Arguments(DesktopPlatform.Windows, new(CaptureKind.PcmAudio, @"\\.\pipe\AravalsStream-audio-test"), 30);
        Assert.Contains("f32le", args); Assert.Contains("48000", args); Assert.Equal(@"\\.\pipe\AravalsStream-audio-test", args[^1]);
    }

    [WindowsAudioFact]
    public async Task NativeLoopbackFeedsAudioTrackAndSilenceDoesNotStallVideo()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ffmpeg = FfmpegProcess.FindExecutable();
        var path = Path.Combine(Path.GetTempPath(), "aravals-loopback-" + Guid.NewGuid().ToString("N") + ".mkv");
        try
        {
            var plan = new MediaPlan(DesktopPlatform.Windows, new(CaptureKind.TestVideo, ""),
                [new(CaptureKind.DesktopAudio, WindowsLoopback.DefaultDevice)], new(640, 360));
            await using (var output = new MediaOutputSession(ffmpeg, plan, path, true))
            {
                await Task.Delay(TimeSpan.FromSeconds(6));
                Assert.False(output.HasExited); Assert.Null(output.Error);
            }
            var probe = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", path }) probe.ArgumentList.Add(argument);
            using var process = Process.Start(probe)!;
            var json = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            await errors; Assert.Equal(0, process.ExitCode);
            using var document = JsonDocument.Parse(await json);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Assert.Contains(streams, s => s.GetProperty("codec_name").GetString() == "aac");
            Assert.Contains(streams, s => s.GetProperty("codec_name").GetString() == "h264");
            Assert.True(double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture) >= 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

public sealed class WindowsAudioFactAttribute : FactAttribute
{
    public WindowsAudioFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ARAVALS_TEST_NATIVE_AUDIO") != "1")
            Skip = "Requires a Windows desktop with a speaker endpoint and explicit native-audio test enablement.";
    }
}
