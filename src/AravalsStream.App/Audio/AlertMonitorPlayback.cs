using AravalsStream.Core.Audio;
using AravalsStream.Core.Services;
using NAudio.Wave;

namespace AravalsStream.App.Audio;

// Reads the existing monitor tap and plays it through the default output device.
public sealed class AlertMonitorPlayback : IDisposable
{
    private readonly AudioMonitoringService _monitor = new();
    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private CancellationTokenSource? _stop;
    private Task? _pump;

    public void Start(AudioMixer mixer)
    {
        lock (_gate)
        {
            if (_output != null) return;
            try
            {
                _monitor.Start(mixer, null);
                _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
                { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true };
                _output = new WaveOutEvent { DesiredLatency = 80 };
                _output.Init(_buffer);
                _output.Play();
                _stop = new CancellationTokenSource();
                _pump = Task.Run(() => PumpAsync(_stop.Token));
            }
            catch (Exception ex)
            {
                AppLog.Write("AlertMonitor", $"Local monitoring unavailable: {ex.Message}");
                _output?.Dispose(); _output = null; _monitor.Stop();
            }
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var floats = new float[960];
        var bytes = new byte[floats.Length * 4];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _monitor.ReadMonitorSamples(floats);
                Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
                _buffer?.AddSamples(bytes, 0, bytes.Length);
                await Task.Delay(10, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("AlertMonitor", $"Monitoring stopped: {ex.Message}"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stop?.Cancel();
            try { _pump?.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _output?.Stop(); _output?.Dispose(); _output = null;
            _monitor.Dispose(); _stop?.Dispose(); _stop = null;
        }
    }
}
