using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;

namespace AravalsStream.App.Composition.Pipelines;

public sealed class CpuVideoPipeline : IVideoPipeline
{
    private readonly ComposedFrameHub _hub;
    private readonly ISceneCompositor _compositor;

    public VideoPipelineMode Mode => VideoPipelineMode.Cpu;
    public string Name => "CPU Video Pipeline (Fallback)";
    public bool IsHardwareAccelerated => false;
    public string PixelFormat => "yuv420p";

    public CpuVideoPipeline(ComposedFrameHub hub, ISceneCompositor compositor)
    {
        _hub = hub;
        _compositor = compositor;
        _hub.PipelineMode = VideoPipelineMode.Cpu;
    }

    public (double RenderMs, double ConvertMs, long DroppedFrames) Diagnostics(OutputMode mode) =>
        _hub.Diagnostics(mode);

    public ComposedFrameHub.Lease Acquire(OutputMode mode, int requestedFps = 60) =>
        _hub.Acquire(mode, requestedFps);

    public void SetScene(Scene? scene, IEnumerable<Scene> allScenes) =>
        _compositor.SetScene(scene, allScenes);

    public void Dispose() { }
}

public sealed class D3D11VideoPipeline : IVideoPipeline
{
    private readonly ComposedFrameHub _hub;
    private readonly ISceneCompositor _compositor;

    public VideoPipelineMode Mode => VideoPipelineMode.Gpu;
    public string Name => "D3D11 Native GPU Video Pipeline";
    public bool IsHardwareAccelerated => true;
    public string PixelFormat => "nv12";

    public D3D11VideoPipeline(ComposedFrameHub hub, ISceneCompositor compositor)
    {
        _hub = hub;
        _compositor = compositor;
        _hub.PipelineMode = VideoPipelineMode.Gpu;
    }

    public (double RenderMs, double ConvertMs, long DroppedFrames) Diagnostics(OutputMode mode) =>
        _hub.Diagnostics(mode);

    public ComposedFrameHub.Lease Acquire(OutputMode mode, int requestedFps = 60) =>
        _hub.Acquire(mode, requestedFps);

    public void SetScene(Scene? scene, IEnumerable<Scene> allScenes) =>
        _compositor.SetScene(scene, allScenes);

    public void Dispose() { }
}

public sealed class VideoPipelineManager : IDisposable
{
    private readonly ComposedFrameHub _hub;
    private readonly ISceneCompositor _compositor;
    private IVideoPipeline _activePipeline;

    public IVideoPipeline ActivePipeline => _activePipeline;
    public VideoPipelineMode Mode
    {
        get => _hub.PipelineMode;
        set
        {
            _hub.PipelineMode = value;
            _activePipeline = value switch
            {
                VideoPipelineMode.Cpu => new CpuVideoPipeline(_hub, _compositor),
                VideoPipelineMode.Gpu => new D3D11VideoPipeline(_hub, _compositor),
                _ => _hub.IsHardwareAccelerated
                    ? new D3D11VideoPipeline(_hub, _compositor)
                    : new CpuVideoPipeline(_hub, _compositor)
            };
        }
    }

    public VideoPipelineManager(ComposedFrameHub hub, ISceneCompositor compositor)
    {
        _hub = hub;
        _compositor = compositor;
        _activePipeline = _hub.IsHardwareAccelerated
            ? new D3D11VideoPipeline(_hub, _compositor)
            : new CpuVideoPipeline(_hub, _compositor);
    }

    public void Dispose()
    {
        _activePipeline.Dispose();
    }
}
