using AravalsStream.App.Composition;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Services;
using AravalsStream.Core.Streaming;

namespace AravalsStream.App.Streaming;

/// <summary>Owns shared encoders and destination-scoped publishers for live outputs.</summary>
public sealed class StreamingOutputGroupManager : IAsyncDisposable
{
    private sealed class GroupContext(EncoderCompatibilityKey key, SharedEncoderSession encoder)
    {
        public EncoderCompatibilityKey Key { get; } = key;
        public SharedEncoderSession Encoder { get; } = encoder;
        public Dictionary<Guid, PublisherSession> Publishers { get; } = [];
    }

    private readonly string _ffmpegPath;
    private readonly ComposedFrameHub _frameHub;
    private readonly AudioMixer _audioMixer;
    private readonly DpapiSecretStorage _secrets;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<EncoderCompatibilityKey, GroupContext> _groups = [];
    private readonly Dictionary<Guid, GroupContext> _destinationGroups = [];
    private bool _disposed;

    public int ActiveCompositorCount => _frameHub.IsHardwareAccelerated ? 1 : 1;
    public int ActiveSharedEncoderCount => _groups.Values.Distinct().Count(g => g.Encoder.IsActive);
    public int ActivePublisherCount => _groups.Values.Distinct().Sum(g => g.Publishers.Values.Count(p => p.Status == DestinationStatus.Live));

    public StreamingOutputGroupManager(string ffmpegPath, ComposedFrameHub frameHub,
        AudioMixer audioMixer, DpapiSecretStorage secrets)
    {
        _ffmpegPath = ffmpegPath;
        _frameHub = frameHub;
        _audioMixer = audioMixer;
        _secrets = secrets;
    }

    public async Task<(PublisherSession Publisher, SharedEncoderSession Encoder)> AcquireAsync(
        Destination destination, string encoderBackend, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_destinationGroups.ContainsKey(destination.Id))
                throw new InvalidOperationException($"Destination {destination.Id} already owns a grouped publisher.");

            var audioFingerprint = AudioMixFingerprint.For(_audioMixer, destination.Id.ToString());
            var key = EncoderCompatibilityKey.For(destination, encoderBackend, audioFingerprint,
                pixelFormat: _frameHub.ActivePixelFormat);

            if (_groups.TryGetValue(key, out var existing) && !existing.Encoder.IsActive)
                throw new InvalidOperationException("The compatible shared encoder has failed. Stop its active outputs, then start them again.");

            var group = existing;
            if (group is null)
            {
                var lease = _frameHub.Acquire(key.CanvasMode, key.FrameRate);
                var encoder = new SharedEncoderSession(key, _ffmpegPath, lease, _audioMixer,
                    destination.Id.ToString());
                group = new GroupContext(key, encoder);
                try
                {
                    await encoder.StartAsync(ct).ConfigureAwait(false);
                    _groups.Add(key, group);
                }
                catch
                {
                    await encoder.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            var streamKey = destination.StreamKeyReference is not null
                ? _secrets.Get(destination.StreamKeyReference) ?? string.Empty
                : string.Empty;
            var publisher = new PublisherSession(destination, _ffmpegPath, streamKey, group.Encoder.PacketHub, group.Encoder.SessionId);
            try
            {
                await publisher.StartAsync(ct).ConfigureAwait(false);
                group.Publishers.Add(destination.Id, publisher);
                _destinationGroups.Add(destination.Id, group);
                AppLog.Write("GroupManager", $"Destination {destination.Platform} joined {group.Key.CanvasMode} shared encoder; session_id={group.Encoder.SessionId}; publishers={group.Publishers.Count}.");
                return (publisher, group.Encoder);
            }
            catch
            {
                await publisher.DisposeAsync().ConfigureAwait(false);
                if (group.Publishers.Count == 0)
                {
                    if (_groups.TryGetValue(group.Key, out var current) && ReferenceEquals(current, group))
                        _groups.Remove(group.Key);
                    await DisposeGroupAsync(group).ConfigureAwait(false);
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ReleaseAsync(Guid destinationId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_destinationGroups.Remove(destinationId, out var group)) return;
            if (group.Publishers.Remove(destinationId, out var publisher))
                await publisher.DisposeAsync().ConfigureAwait(false);

            if (group.Publishers.Count == 0)
            {
                if (_groups.TryGetValue(group.Key, out var current) && ReferenceEquals(current, group))
                    _groups.Remove(group.Key);
                await DisposeGroupAsync(group).ConfigureAwait(false);
                AppLog.Write("GroupManager", $"Last publisher left {group.Key.CanvasMode}; session_id={group.Encoder.SessionId}; shared encoder disposed.");
            }
            else
            {
                AppLog.Write("GroupManager", $"Destination {destinationId} left shared encoder; session_id={group.Encoder.SessionId}; remaining={group.Publishers.Count}.");
            }
        }
        finally { _gate.Release(); }
    }

    public (int Encoders, int Publishers) SnapshotCounts => (ActiveSharedEncoderCount,
        _groups.Values.Distinct().Sum(g => g.Publishers.Count));

    private static async Task DisposeGroupAsync(GroupContext group)
    {
        foreach (var publisher in group.Publishers.Values)
        {
            try { await publisher.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        group.Publishers.Clear();
        try { await group.Encoder.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var group in _groups.Values.ToArray())
                await DisposeGroupAsync(group).ConfigureAwait(false);
            _groups.Clear();
            _destinationGroups.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }
}
