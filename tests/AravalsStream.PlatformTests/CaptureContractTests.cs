using AravalsStream.Platform;
using Xunit;
using AravalsStream.Core.Models;

namespace AravalsStream.PlatformTests;
public sealed class CaptureContractTests
{
    [Theory]
    [InlineData(DesktopPlatform.Windows, "gdigrab", "desktop", "desktop")]
    [InlineData(DesktopPlatform.LinuxX11, "x11grab", ":0.0", ":0.0")]
    [InlineData(DesktopPlatform.MacOS, "avfoundation", "2", "2:none")]
    public void DisplayUsesTheNativeDeviceContract(DesktopPlatform platform, string backend, string device, string expected)
    {
        var args = PlatformCapture.Arguments(platform, new(CaptureKind.Display, device), 30);
        Assert.Contains(backend, args); Assert.Equal(expected, args[^1]);
    }
    [Fact]
    public void WaylandNeverSilentlyFallsBackToX11() => Assert.Throws<PlatformNotSupportedException>(() =>
        PlatformCapture.Arguments(DesktopPlatform.LinuxWayland, new(CaptureKind.Display, ":0.0"), 30));
    [Theory]
    [InlineData(DesktopPlatform.Windows)]
    [InlineData(DesktopPlatform.MacOS)]
    public void MicrophoneIsNotPresentedAsLoopback(DesktopPlatform platform) => Assert.Throws<PlatformNotSupportedException>(() =>
        PlatformCapture.Arguments(platform, new(CaptureKind.DesktopAudio, "default"), 30));
    [Fact]
    public void DeviceNamesStaySingleArguments()
    {
        var args = PlatformCapture.Arguments(DesktopPlatform.Windows, new(CaptureKind.Camera, "Camera name & $(command)"), 30);
        Assert.Equal("video=Camera name & $(command)", args[^1]);
    }
    [Fact]
    public void AudioMixUsesEverySelectedInputAndMute()
    {
        var plan = Plan() with { Audio = [new(CaptureKind.TestAudio, "", 0.5f), new(CaptureKind.TestAudio, "", Muted: true)] };
        var args = MediaArguments.Output(plan, "rtmp://localhost/live/test", false).ToArray();
        var filter = args[Array.IndexOf(args, "-filter_complex") + 1];
        Assert.Contains("volume=0.5", filter); Assert.Contains("volume=0[a1]", filter); Assert.Contains("amix=inputs=2", filter);
    }
    [Theory]
    [InlineData("https://example.org/live")]
    [InlineData("rtmp://user:password@example.org/live")]
    [InlineData("invalid")]
    public void InvalidOrCredentialedUrlsFailBeforeStarting(string url) => Assert.Throws<ArgumentException>(() => MediaArguments.Output(Plan(), url, false));
    [Theory]
    [InlineData(1921, 1080)]
    [InlineData(0, 1080)]
    public void InvalidCanvasFailsBeforeStarting(int width, int height) => Assert.Throws<ArgumentException>(() =>
        MediaArguments.Output(Plan() with { Canvas = new(width, height) }, "rtmp://localhost/live/test", false));
    [Fact]
    public void VerticalOutputUsesPortraitDimensions() => Assert.Contains("scale=1080:1920", string.Join(' ',
        MediaArguments.Output(Plan() with { Canvas = CanvasSize.Vertical }, "rtmp://localhost/live/test", false)));
    [Fact]
    public void CompositionKeepsAudioIndicesAfterVideoInputs()
    {
        var plan = Plan() with { Layers = [new(new(CaptureKind.TestVideo, ""), new() { Width = 320, Height = 180, X = 50, Y = 30, Opacity = 0.5 })] };
        var args = MediaArguments.Output(plan, "rtmp://localhost/live/test", false).ToArray();
        var filter = args[Array.IndexOf(args, "-filter_complex") + 1];
        Assert.Contains("[2:a:0]", filter); Assert.Contains("scale=320:180", filter); Assert.Contains("aa=0.5", filter);
        Assert.Contains("overlay=x=50", filter); Assert.DoesNotContain("-vf", args);
    }
    [Fact]
    public void HiddenSourcesAreNotOpened()
    {
        var plan = Plan() with { Layers = [new(new(CaptureKind.Camera, "private-camera"), new(), false)] };
        var args = MediaArguments.Preview(plan); Assert.DoesNotContain("video=private-camera", args);
    }
    [Fact]
    public void NonFiniteTransformFailsBeforeStarting()
    {
        var plan = Plan() with { Layers = [new(new(CaptureKind.TestVideo, ""), new SourceTransform { X = double.NaN })] };
        Assert.Throws<ArgumentException>(() => MediaArguments.Preview(plan));
    }
    private static MediaPlan Plan() => new(PlatformCapture.Current, new(CaptureKind.TestVideo, ""), [new(CaptureKind.TestAudio, "")], new(640, 360));
}
