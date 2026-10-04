using System.Windows.Threading;
using AravalsStream.App.Composition;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using Xunit;

namespace AravalsStream.DesktopTests;

[CollectionDefinition("Custom canvas composition", DisableParallelization = true)]
public class CustomCanvasCompositionCollection;

[Collection("Custom canvas composition")]
public class CustomCanvasCompositionTests
{
    [Theory]
    [InlineData(OutputMode.Horizontal)]
    [InlineData(OutputMode.Vertical)]
    public void ProductionCompositorUsesCustomLogicalDimensions(OutputMode mode)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                CanvasLayout.Configure(new CanvasSettings { HorizontalWidth = 1280, HorizontalHeight = 720,
                    VerticalWidth = 720, VerticalHeight = 1280 });
                using var compositor = new SceneCompositor(Dispatcher.CurrentDispatcher);
                var source = new SceneSource { Type = SourceType.ChatOverlay };
                var transform = mode == OutputMode.Vertical ? source.VerticalTransform : source.HorizontalTransform;
                var size = CanvasLayout.Size(mode);
                transform.Width = size.Width; transform.Height = size.Height;
                var scene = new Scene(); scene.Sources.Add(source);
                compositor.SetScene(scene, [scene]);
                compositor.SetOverlay(SourceType.ChatOverlay, mode,
                    new RawVideoFrame(2, 2, 8, Enumerable.Repeat((byte)255, 16).ToArray()));
                var pixels = new byte[64 * 64 * 4];
                compositor.RenderComposedFrame(mode, 64, 64, pixels);
                // A stale 1920x1080 logical canvas leaves the right/bottom edge black.
                Assert.Equal(255, pixels[(63 * 64 + 63) * 4]);
                Assert.Equal(255, pixels[(63 * 64 + 63) * 4 + 1]);
                Assert.Equal(255, pixels[(63 * 64 + 63) * 4 + 2]);
            }
            catch (Exception ex) { error = ex; }
            finally { CanvasLayout.Configure(new()); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw error;
    }
}
