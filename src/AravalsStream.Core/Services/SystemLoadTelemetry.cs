using System.Diagnostics;

namespace AravalsStream.Core.Services;

public sealed class SystemLoadTelemetry
{
    private DateTime _lastCpuSampleTime = DateTime.MinValue;
    private TimeSpan _lastCpuTime = TimeSpan.Zero;
    private double _lastCpuUsagePercent = 0.0;
    private readonly Process _currentProcess = Process.GetCurrentProcess();

    public double CpuUsagePercent
    {
        get
        {
            var now = DateTime.UtcNow;
            if (_lastCpuSampleTime == DateTime.MinValue)
            {
                _lastCpuSampleTime = now;
                _lastCpuTime = _currentProcess.TotalProcessorTime;
                return 0.0;
            }

            var elapsedWallClock = now - _lastCpuSampleTime;
            if (elapsedWallClock.TotalMilliseconds < 500)
                return _lastCpuUsagePercent;

            var currentCpuTime = _currentProcess.TotalProcessorTime;
            var cpuUsedMs = (currentCpuTime - _lastCpuTime).TotalMilliseconds;
            var totalAvailableMs = elapsedWallClock.TotalMilliseconds * Environment.ProcessorCount;

            _lastCpuUsagePercent = Math.Clamp((cpuUsedMs / totalAvailableMs) * 100.0, 0.0, 100.0);
            _lastCpuSampleTime = now;
            _lastCpuTime = currentCpuTime;

            return _lastCpuUsagePercent;
        }
    }

    public long MemoryUsedBytes => _currentProcess.WorkingSet64;
    public double MemoryUsedMb => MemoryUsedBytes / (1024.0 * 1024.0);

    public static (bool IsOverloaded, string? Warning, string[] Suggestions) CheckEncoderPerformance(double currentFps, double targetFps)
    {
        if (targetFps <= 0) return (false, null, []);
        double ratio = currentFps / targetFps;
        if (ratio < 0.75 && currentFps > 0)
        {
            return (
                true,
                $"Encoder overloaded: running at {currentFps:0.0} FPS ({ratio * 100:0}% of target {targetFps:0} FPS).",
                new[]
                {
                    "Lower output Frame Rate (e.g. 60 FPS -> 30 FPS)",
                    "Lower output canvas resolution",
                    "Select a dedicated Hardware Encoder (NVENC, QSV, AMF)",
                    "Reduce the number of simultaneous active streams"
                }
            );
        }
        return (false, null, []);
    }
}
