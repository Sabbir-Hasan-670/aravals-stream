using System.Text.Json;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using AravalsStream.Core.Audio;
using Xunit;

namespace AravalsStream.Tests;

[CollectionDefinition("Custom canvas", DisableParallelization = true)]
public class CustomCanvasCollection;

[Collection("Custom canvas")]
public class CustomCanvasSettingsTests
{
    [Fact]
    public void CustomCanvasDrivesFitAndCrossCanvasLayout()
    {
        try
        {
            CanvasLayout.Configure(new CanvasSettings { HorizontalWidth = 2560, HorizontalHeight = 1440,
                VerticalWidth = 720, VerticalHeight = 1280 });
            var transform = new SourceTransform();
            CanvasLayout.Fit(transform, 1920, 1080, OutputMode.Horizontal);
            Assert.Equal(2560, transform.Width);
            Assert.Equal(1440, transform.Height);
            Assert.Equal((1280d, 720d), CanvasLayout.PreviewToCanvas(640, 360, 1280, 720, OutputMode.Horizontal));
            var vertical = new SourceTransform();
            CanvasLayout.CopyLayout(transform, vertical, OutputMode.Horizontal, OutputMode.Vertical);
            Assert.Equal(720, vertical.Width);
            Assert.Equal(1280, vertical.Height);
            Assert.Equal((720, 1280), CanvasLayout.Size(OutputMode.Vertical));
        }
        finally { CanvasLayout.Configure(new()); }
    }

    [Theory]
    [InlineData(1279, 720)]
    [InlineData(0, 720)]
    [InlineData(8192, 4320)]
    public void InvalidSizesCannotReplaceActiveCanvas(int width, int height)
    {
        var previous = CanvasLayout.Size(OutputMode.Horizontal);
        Assert.Throws<ArgumentException>(() => CanvasLayout.Configure(new CanvasSettings
            { HorizontalWidth = width, HorizontalHeight = height }));
        Assert.Equal(previous, CanvasLayout.Size(OutputMode.Horizontal));
    }

    [Fact]
    public void CustomCanvasAndSharedMicrophoneProfilePersist()
    {
        var settings = new AppSettings { Canvas = new CanvasSettings { HorizontalWidth = 1280, HorizontalHeight = 720 },
            Audio = new AudioSettings { DefaultMicrophoneId = "mic-2", MicrophoneFilters = new AudioFilterSettings { NoiseSuppression = true, Gain = true, GainDb = 4 } } };
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(1280, loaded.Canvas.HorizontalWidth);
        Assert.Equal(720, loaded.Canvas.HorizontalHeight);
        Assert.Equal("mic-2", loaded.Audio.DefaultMicrophoneId);
        Assert.True(loaded.Audio.MicrophoneFilters!.NoiseSuppression);
        Assert.Equal(4, loaded.Audio.MicrophoneFilters.GainDb);
    }
}
