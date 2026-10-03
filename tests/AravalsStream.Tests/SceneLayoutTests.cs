using System.Text.Json;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using Xunit;

namespace AravalsStream.Tests;

public sealed class SceneLayoutTests
{
    [Fact]
    public void PreviewCoordinatesMapToLogicalCanvas()
    {
        Assert.Equal((200d, 100d), CanvasLayout.PreviewToCanvas(100, 50, 960, 540, OutputMode.Horizontal));
        Assert.Equal((540d, 960d), CanvasLayout.PreviewToCanvas(270, 480, 540, 960, OutputMode.Vertical));
    }

    [Fact]
    public void FitAndCenterPreserveAspectRatio()
    {
        var transform = new SourceTransform();
        CanvasLayout.Fit(transform, 2560, 1440, OutputMode.Horizontal);
        Assert.Equal(1920, transform.Width, 5);
        Assert.Equal(1080, transform.Height, 5);
        Assert.Equal(0, transform.X, 5);
        CanvasLayout.Fit(transform, 2560, 1440, OutputMode.Vertical);
        Assert.Equal(1080, transform.Width, 5);
        Assert.Equal(607.5, transform.Height, 5);
        Assert.Equal(656.25, transform.Y, 5);
    }

    [Fact]
    public void CopyLayoutScalesPositionToTargetCanvas()
    {
        var from = new SourceTransform { X = 960, Y = 540, Width = 480, Height = 270 };
        var to = new SourceTransform();
        CanvasLayout.CopyLayout(from, to, OutputMode.Horizontal, OutputMode.Vertical);
        Assert.Equal(540, to.X);
        Assert.Equal(960, to.Y);
        Assert.Equal(270, to.Width);
        Assert.Equal(480, to.Height);
    }

    [Fact]
    public void CanvasTransformsAndSourceOrderRemainIndependent()
    {
        var scene = new Scene();
        var first = new SceneSource { Name = "First" };
        var second = new SceneSource { Name = "Second" };
        scene.Sources.Add(first); scene.Sources.Add(second);
        first.HorizontalTransform.X = 500;
        first.VerticalTransform.X = 20;
        scene.Sources.Move(0, 1);
        Assert.Equal(20, first.VerticalTransform.X);
        Assert.Equal(500, first.HorizontalTransform.X);
        Assert.Equal(second.Id, scene.Sources[0].Id);
        Assert.Equal(first.Id, scene.Sources[1].Id);
    }

    [Fact]
    public void LayoutAndSourceIdentitySurviveSerialization()
    {
        var resource = new DisplayCaptureSource { DisplayId = @"\\.\DISPLAY1" };
        var source = new SceneSource { SourceReference = resource.Id, DisplayId = resource.DisplayId, Visible = false };
        source.HorizontalTransform.X = 230;
        source.VerticalTransform.X = -470;
        source.VerticalTransform.CropLeft = 100;
        source.VerticalTransform.Locked = true;
        var scene = new Scene(); scene.Sources.Add(source);
        var settings = new AppSettings { Scenes = [scene], CaptureSources = [resource] };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        var item = restored.Scenes[0].Sources[0];
        Assert.Equal(resource.Id, item.SourceReference);
        Assert.False(item.Visible);
        Assert.Equal(230, item.HorizontalTransform.X);
        Assert.Equal(-470, item.VerticalTransform.X);
        Assert.Equal(100, item.VerticalTransform.CropLeft);
        Assert.True(item.VerticalTransform.Locked);
    }
}
