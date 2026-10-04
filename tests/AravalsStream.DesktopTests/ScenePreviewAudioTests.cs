using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AravalsStream.App.Composition;
using AravalsStream.App.Controls;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;
using Xunit;

namespace AravalsStream.DesktopTests;

public sealed class ScenePreviewAudioTests
{
    [Fact]
    public void AudioSelectionHasNoAdornersOrHitTestVisuals()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var audio = new SceneSource { Type = SourceType.AudioInput };
                var video = new SceneSource { Type = SourceType.DisplayCapture };
                var scene = new Scene();
                scene.Sources.Add(audio);
                scene.Sources.Add(video);
                var preview = new ScenePreview();
                preview.Bind(new EmptyCompositor());
                preview.SetScene(scene);
                Assert.Single((System.Collections.IList)Field(preview, "_items")!);

                preview.SetSelected(audio);
                Assert.Null(Field(preview, "_selected"));
                Assert.Empty(((Canvas)Field(preview, "_adornerCanvas")!).Children);

                preview.SetSelected(video);
                Assert.Same(video, Field(preview, "_selected"));
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);

    private sealed class EmptyCompositor : ISceneCompositor
    {
        public Scene? ActiveScene => null;
        public event Action? CompositionChanged { add { } remove { } }
        public event Action<string>? FrameReady { add { } remove { } }
        public event Action<string, string>? SourceFailed { add { } remove { } }
        public void SetResources(IEnumerable<CaptureResource> resources) { }
        public string? KeyFor(SceneSource source) => null;
        public void SetScene(Scene? scene, IEnumerable<Scene> allScenes) { }
        public void RefreshSources(IEnumerable<Scene> allScenes) { }
        public void RestartResource(Guid resourceId, IEnumerable<Scene> allScenes) { }
        public BitmapSource? FrameFor(SceneSource source) => null;
        public RawVideoFrame? RawFrameFor(SceneSource source) => null;
        public void RenderComposedFrame(OutputMode mode, int targetWidth, int targetHeight, byte[] destinationBuffer,
            CanvasRenderCache? renderCache = null) { }
        public SceneSourceFrameLease? AcquireSourceFrame(SceneSource source, OutputMode mode) => null;
        public void Dispose() { }
    }
}
