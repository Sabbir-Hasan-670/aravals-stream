using AravalsStream.Core.Models;
using AravalsStream.Core.Composition;

namespace AravalsStream.App.Composition.Pipelines;

public enum VideoPipelineMode
{
    Auto,
    Gpu,
    Cpu
}

public interface IVideoPipeline : IDisposable
{
    VideoPipelineMode Mode { get; }
    string Name { get; }
    bool IsHardwareAccelerated { get; }
    string PixelFormat { get; } // "nv12" for GPU, "yuv420p" for CPU
    (double RenderMs, double ConvertMs, long DroppedFrames) Diagnostics(OutputMode mode);
    ComposedFrameHub.Lease Acquire(OutputMode mode, int requestedFps = 60);
    void SetScene(Scene? scene, IEnumerable<Scene> allScenes);
}
