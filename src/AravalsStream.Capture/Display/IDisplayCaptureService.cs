namespace AravalsStream.Capture.Display;

public interface IDisplayCaptureService : IDisposable
{
    IReadOnlyList<DisplayInfo> EnumerateDisplays();
    IDisplayCaptureSession Start(DisplayInfo display);
}

public interface IDisplayCaptureSession : Video.IVideoCaptureSession
{
    DisplayInfo Display { get; }
    int TargetFps { get; set; }
    void Stop();
}
