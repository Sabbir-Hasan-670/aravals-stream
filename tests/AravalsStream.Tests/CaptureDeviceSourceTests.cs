using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using Xunit;

namespace AravalsStream.Tests;

public sealed class CaptureDeviceSourceTests
{
    [Fact]
    public void CaptureDeviceUsesOnlyEnumeratedFormatsAndTheRecommendedFormatIsSupported()
    {
        var advertised = new[]
        {
            new VideoFormatChoice(1920, 1080, 60, "NV12"),
            new VideoFormatChoice(1280, 720, 30, "YUY2"),
            new VideoFormatChoice(0, 1080, 30, "NV12")
        };

        var selected = CameraFormatSelector.Recommended(advertised);

        Assert.Contains(selected, advertised);
        Assert.Equal(1280, selected!.Width);
        Assert.Equal(720, selected.Height);
        Assert.Equal(30, selected.FramesPerSecond);
    }

    [Fact]
    public void CaptureDeviceSourceParticipatesInTheNormalVisualCaptureDemand()
    {
        var scene = new Scene();
        var source = new SceneSource { Type = SourceType.CaptureDevice, Visible = true };
        scene.Sources.Add(source);
        var keys = CaptureDemand.ActiveResourceKeys(scene, s => s.SourceReference.ToString());
        Assert.Contains(source.SourceReference.ToString(), keys);

        source.Visible = false;
        Assert.Empty(CaptureDemand.ActiveResourceKeys(scene, s => s.SourceReference.ToString()));
    }

    [Fact]
    public void NewSourceTypesDoNotChangeExistingPersistedEnumValues()
    {
        Assert.Equal(0, (int)SourceType.DisplayCapture);
        Assert.Equal(3, (int)SourceType.Camera);
        Assert.Equal(8, (int)SourceType.Alerts);
        Assert.Equal(9, (int)SourceType.AudioInput);
        Assert.Equal(12, (int)SourceType.ChatOverlay);
        Assert.True((int)SourceType.CaptureDevice > (int)SourceType.ChatOverlay);
    }
}
