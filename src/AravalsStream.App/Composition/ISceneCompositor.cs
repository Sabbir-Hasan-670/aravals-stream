using System.Windows.Media.Imaging;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;

namespace AravalsStream.App.Composition;

public interface ISceneCompositor : IDisposable
{
    Scene? ActiveScene { get; }
    event Action? CompositionChanged;
    event Action<string>? FrameReady;
    event Action<string, string>? SourceFailed;
    void SetResources(IEnumerable<CaptureResource> resources);
    string? KeyFor(SceneSource source);
    void SetScene(Scene? scene, IEnumerable<Scene> allScenes);
    void RefreshSources(IEnumerable<Scene> allScenes);
    void RestartResource(Guid resourceId, IEnumerable<Scene> allScenes);
    BitmapSource? FrameFor(SceneSource source);
    RawVideoFrame? RawFrameFor(SceneSource source);
    void RenderComposedFrame(OutputMode mode, int targetWidth, int targetHeight, byte[] destinationBuffer,
        CanvasRenderCache? renderCache = null);
    SceneSourceFrameLease? AcquireSourceFrame(SceneSource source, OutputMode mode);
}

/// <summary>A stable read lease for one captured or rendered source frame.</summary>
public sealed class SceneSourceFrameLease : IDisposable
{
    private IDisposable? _owner;

    public RawVideoFrame Frame { get; }
    public long Version { get; }

    public SceneSourceFrameLease(RawVideoFrame frame, long version, IDisposable owner)
    {
        Frame = frame;
        Version = version;
        _owner = owner;
    }

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
}
