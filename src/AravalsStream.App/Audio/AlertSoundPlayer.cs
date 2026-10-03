using System.IO;
using AravalsStream.Core.Alerts;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AravalsStream.App.Audio;

public sealed class AlertSoundPlayer : IDisposable
{
    private readonly AudioMixer _mixer;
    private readonly AlertMonitorPlayback _monitor = new();
    private readonly Func<bool> _canMonitor;
    private readonly AudioChannel _channel = new() { Name = "Alert Sound", Active = true };
    public AlertSoundPlayer(AudioMixer mixer, Func<bool> canMonitor)
    { _mixer = mixer; _canMonitor = canMonitor; }

    public async Task PlayAsync(AlertDefinition definition, IEnumerable<PlatformDestinationGroup> destinations,
        CancellationToken ct = default)
    {
        var path = definition.SoundPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (!new[] { ".wav", ".mp3" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            _channel.Volume = (float)Math.Clamp(definition.SoundVolume, 0, 1);
            var monitor = definition.MonitorSound && _canMonitor();
            if (monitor) _monitor.Start(_mixer);
            else if (definition.MonitorSound) AppLog.Write("AlertSound", "Local monitor skipped because desktop output capture is active.");
            _channel.MonitoringMode = monitor
                ? AudioMonitoringMode.MonitorAndOutput : AudioMonitoringMode.MonitorOff;
            AlertSoundRouting.Configure(_mixer.Matrix, _channel.Id, definition, destinations);
            await Task.Run(async () =>
            {
                using var reader = new AudioFileReader(path);
                ISampleProvider provider = reader;
                if (provider.WaveFormat.Channels == 1) provider = new MonoToStereoSampleProvider(provider);
                if (provider.WaveFormat.Channels != 2) throw new InvalidDataException("Alert sound needs mono or stereo audio.");
                if (provider.WaveFormat.SampleRate != 48000) provider = new WdlResamplingSampleProvider(provider, 48000);
                var buffer = new float[960]; // 10 ms, 48 kHz stereo
                while (!ct.IsCancellationRequested)
                {
                    var count = provider.Read(buffer, 0, buffer.Length);
                    if (count <= 0) break;
                    _mixer.Receive(_channel, buffer.AsSpan(0, count));
                    await Task.Delay(10, ct);
                }
            }, ct);
        }
        catch (Exception ex) { AppLog.Write("AlertSound", $"Sound skipped: {ex.Message}"); }
    }

    public void Dispose() => _monitor.Dispose();
}
