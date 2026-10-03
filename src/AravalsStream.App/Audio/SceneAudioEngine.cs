using System.Collections.ObjectModel;
using System.Diagnostics;
using AravalsStream.Capture.Audio;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using AravalsStream.App.RemoteCapture;

namespace AravalsStream.App.Audio;

public sealed class SceneAudioEngine : IDisposable
{
    private sealed record Feed(IAudioCaptureSession Session, AudioChannel Channel);
    private sealed record RemoteFeed(RemoteSrtCaptureSession Session, Action<float[]> Handler, AudioChannel Channel);
    private readonly IAudioCaptureService _capture = new WasapiAudioCaptureService();
    private readonly Dictionary<Guid, CaptureResource> _resources = [];
    private readonly Dictionary<Guid, Feed> _feeds = [];
    private readonly Dictionary<Guid, RemoteFeed> _remoteFeeds = [];
    private readonly RemoteCaptureSessionFactory? _remoteCapture;
    private HashSet<Guid> _active = [];
    private Scene? _scene;
    private bool _disposed;
    public AudioMixer Mixer { get; } = new();
    public ObservableCollection<AudioChannel> Channels { get; } = [];
    public event Action<Guid, string>? SourceFailed;
    public event Action<Guid>? SourceStarted;

    public SceneAudioEngine(RemoteCaptureSessionFactory? remoteCapture = null)
    {
        _remoteCapture = remoteCapture;
        if (_remoteCapture is not null) _remoteCapture.SessionReplaced += OnRemoteSessionReplaced;
    }

    public void SetResources(IEnumerable<CaptureResource> resources)
    {
        _resources.Clear();
        foreach (var resource in resources) _resources[resource.Id] = resource;
    }

    public void SetScene(Scene? scene, IEnumerable<Scene> allScenes)
    {
        _scene = scene;
        RefreshSources(allScenes);
    }

    public void RefreshSources(IEnumerable<Scene> allScenes)
    {
        if (_disposed) return;
        var referenced = allScenes.SelectMany(s => s.Sources)
            .Where(s => s.Visible && IsAudio(s.Type)).Select(s => s.SourceReference).ToHashSet();
        foreach (var stale in _feeds.Keys.Concat(_remoteFeeds.Keys).Where(id => !referenced.Contains(id)).Distinct().ToList()) Stop(stale);
        foreach (var channel in Channels.Where(c => !referenced.Contains(c.Id)).ToList()) Channels.Remove(channel);
        var active = _scene?.Sources.Where(s => s.Visible && IsAudio(s.Type))
            .Select(s => s.SourceReference).ToHashSet() ?? [];
        Volatile.Write(ref _active, active);
        foreach (var id in active)
        {
            if (_feeds.ContainsKey(id) || _remoteFeeds.ContainsKey(id) || !_resources.TryGetValue(id, out var resource)) continue;
            var channel = Channels.FirstOrDefault(c => c.Id == id);
            if (channel is null)
            {
                channel = new AudioChannel { Id = id, Name = resource.Name, Volume = resource.Volume, Muted = resource.Muted };
                channel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AudioChannel.Volume)) resource.Volume = channel.Volume;
                    if (e.PropertyName == nameof(AudioChannel.Muted)) resource.Muted = channel.Muted;
                };
                Channels.Add(channel);
            }
            try
            {
                if (resource.Type == SourceType.RemotePc)
                {
                    var remote = _remoteCapture?.Get(id) ?? throw new InvalidOperationException("Remote video session is not active.");
                    channel.Name = $"REMOTE — {resource.Name.Replace("Remote PC — ", "", StringComparison.OrdinalIgnoreCase)} AUDIO";
                    channel.Active = true;
                    Action<float[]> handler = CreateRemoteAudioHandler(id, channel);
                    remote.AudioSamplesArrived += handler;
                    remote.AudioFailed += message => channel.Active = false;
                    _remoteFeeds.Add(id, new RemoteFeed(remote, handler, channel));
                    SourceStarted?.Invoke(id);
                    continue;
                }
                var info = new AudioDeviceInfo(resource.DeviceId, resource.Name, resource.Type == SourceType.AudioInput, false);
                var session = _capture.Start(info);
                channel.Active = true;
                session.SamplesArrived += samples =>
                {
                    if (Volatile.Read(ref _active).Contains(id)) Mixer.Receive(channel, samples);
                };
                session.CaptureFailed += ex => SourceFailed?.Invoke(id, ex.Message);
                _feeds.Add(id, new Feed(session, channel));
                SourceStarted?.Invoke(id);
                AppLog.Write("Audio", $"Audio source opened: {resource.Name}; {session.InputFormat.SampleRate} Hz/{session.InputFormat.Channels} channels");
            }
            catch (Exception ex)
            {
                channel.Active = false;
                AppLog.Write("Audio", $"Audio source failed: {resource.Name}: {ex.Message}");
                SourceFailed?.Invoke(id, ex.Message);
            }
        }
        foreach (var (id, feed) in _feeds) feed.Channel.Active = active.Contains(id);
        foreach (var (id, feed) in _remoteFeeds) feed.Channel.Active = active.Contains(id);
    }

    private static bool IsAudio(SourceType type) => type is SourceType.AudioInput or SourceType.AudioOutput or SourceType.RemotePc;

    private Action<float[]> CreateRemoteAudioHandler(Guid id, AudioChannel channel)
    {
        var receivedBlocks = 0;
        return samples =>
        {
            if (!Volatile.Read(ref _active).Contains(id)) return;
            Mixer.Receive(channel, samples);
            if (Interlocked.Increment(ref receivedBlocks) == 1)
            {
                var peak = 0f;
                double sumSquares = 0;
                foreach (var sample in samples) { var abs = Math.Abs(sample); peak = Math.Max(peak, abs); sumSquares += sample * sample; }
                var rms = samples.Length == 0 ? 0 : Math.Sqrt(sumSquares / samples.Length);
                AppLog.Write("RemoteMedia", $"FirstAudioBlockPublishedToMixer at {Stopwatch.GetTimestamp()}, source={id:N}, sampleCount={samples.Length}, peak={peak:F6}, rms={rms:F6}");
            }
        };
    }

    private void OnRemoteSessionReplaced(Guid id, RemoteSrtCaptureSession replacement)
    {
        if (_disposed || !_remoteFeeds.TryGetValue(id, out var current)) return;
        current.Session.AudioSamplesArrived -= current.Handler;
        var handler = CreateRemoteAudioHandler(id, current.Channel);
        replacement.AudioSamplesArrived += handler;
        replacement.AudioFailed += _ => current.Channel.Active = false;
        current.Channel.Active = Volatile.Read(ref _active).Contains(id);
        _remoteFeeds[id] = new RemoteFeed(replacement, handler, current.Channel);
        AppLog.Write("RemoteMedia", $"RemoteAudioSessionRebound at {Stopwatch.GetTimestamp()}, source={id:N}");
    }

    private void Stop(Guid id)
    {
        if (_remoteFeeds.Remove(id, out var remote))
        {
            remote.Session.AudioSamplesArrived -= remote.Handler;
            Mixer.RemoveChannel(id);
            Channels.Remove(remote.Channel);
            return;
        }
        if (!_feeds.Remove(id, out var feed)) return;
        feed.Session.Dispose();
        Mixer.RemoveChannel(id);
        Channels.Remove(feed.Channel);
        AppLog.Write("Audio", $"Audio source closed: {feed.Channel.Name}");
    }

    public void StopFailed(Guid id)
    {
        if (_remoteFeeds.Remove(id, out var remote))
        {
            remote.Session.AudioSamplesArrived -= remote.Handler;
            remote.Channel.Active = false;
            Mixer.RemoveChannel(id);
            return;
        }
        if (!_feeds.Remove(id, out var feed)) return;
        feed.Channel.Active = false;
        feed.Session.Dispose();
        Mixer.RemoveChannel(id);
    }

    public void RestartResource(Guid id, IEnumerable<Scene> allScenes)
    {
        StopFailed(id);
        RefreshSources(allScenes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_remoteCapture is not null) _remoteCapture.SessionReplaced -= OnRemoteSessionReplaced;
        Volatile.Write(ref _active, []);
        foreach (var id in _feeds.Keys.ToList()) Stop(id);
    }
}

