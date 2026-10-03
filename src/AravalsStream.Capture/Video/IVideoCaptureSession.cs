using AravalsStream.Capture.Display;

namespace AravalsStream.Capture.Video;

public interface IVideoCaptureSession : IDisposable
{
    event EventHandler<DisplayFrame>? FrameArrived;
    event EventHandler<Exception>? CaptureFailed;
}
