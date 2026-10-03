namespace AravalsStream.Core.Audio;

public sealed class AudioMonitoringService : IDisposable
{
    private readonly object _gate = new();
    private AudioOutputTap? _monitorTap;
    private bool _isRunning;
    private string? _selectedDeviceId;

    public bool IsRunning => _isRunning;
    public string? SelectedDeviceId => _selectedDeviceId;

    public static bool DetectFeedbackLoop(string? monitoringDeviceId, IEnumerable<string?> activeCaptureDeviceIds)
    {
        if (string.IsNullOrWhiteSpace(monitoringDeviceId)) return false;
        foreach (var capId in activeCaptureDeviceIds)
        {
            if (string.IsNullOrWhiteSpace(capId)) continue;
            if (string.Equals(monitoringDeviceId.Trim(), capId.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void Start(AudioMixer mixer, string? deviceId)
    {
        lock (_gate)
        {
            Stop();
            _selectedDeviceId = deviceId;
            _monitorTap = mixer.CreateOutputTap("Monitor");
            _isRunning = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _isRunning = false;
            _monitorTap?.Dispose();
            _monitorTap = null;
        }
    }

    public void ReadMonitorSamples(Span<float> buffer)
    {
        lock (_gate)
        {
            if (_monitorTap != null && _isRunning)
            {
                _monitorTap.Read(buffer);
            }
            else
            {
                buffer.Clear();
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
