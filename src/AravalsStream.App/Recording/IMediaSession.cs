using AravalsStream.Core.Models;
using AravalsStream.Core.Recording.Models;

namespace AravalsStream.App.Recording;

public interface IMediaSession : IAsyncDisposable
{
    OutputMode Mode { get; }
    string OutputPath { get; }
    RecordingState State { get; }
    RecordingTelemetry Telemetry { get; }
    event Action<RecordingState>? StateChanged;
    event Action<string>? ErrorOccurred;

    Task StartAsync(CancellationToken cancellationToken = default);
    void Pause();
    void Resume();
    Task StopAsync(CancellationToken cancellationToken = default);
}
