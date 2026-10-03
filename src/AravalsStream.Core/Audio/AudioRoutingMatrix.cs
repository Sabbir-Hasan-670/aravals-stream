using System.Collections.Concurrent;
using AravalsStream.Core.Settings;

namespace AravalsStream.Core.Audio;

public sealed class AudioRoutingMatrix
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid ChannelId, string TargetKey), (bool Enabled, float RouteGain)> _routes = [];

    public event Action? RoutesChanged;

    public void SetRoute(Guid channelId, string targetKey, bool enabled, float routeGain = 1.0f)
    {
        lock (_gate)
        {
            _routes[(channelId, targetKey)] = (enabled, Math.Clamp(routeGain, 0f, 2f));
        }
        RoutesChanged?.Invoke();
    }

    public bool IsRouteEnabled(Guid channelId, string targetKey)
    {
        lock (_gate)
        {
            if (_routes.TryGetValue((channelId, targetKey), out var entry))
                return entry.Enabled;
            return true; // Default: ON for all outputs
        }
    }

    public float GetRouteGain(Guid channelId, string targetKey)
    {
        lock (_gate)
        {
            if (_routes.TryGetValue((channelId, targetKey), out var entry))
                return entry.RouteGain;
            return 1.0f; // Default: 100%
        }
    }

    public float GetEffectiveGain(Guid channelId, string targetKey, float sourceVolume, bool sourceMuted)
    {
        if (sourceMuted) return 0f;
        if (!IsRouteEnabled(channelId, targetKey)) return 0f;
        return Math.Clamp(sourceVolume * GetRouteGain(channelId, targetKey), 0f, 2f);
    }

    public List<AudioRouteSetting> ExportSettings(Dictionary<Guid, string>? channelNames = null)
    {
        var list = new List<AudioRouteSetting>();
        lock (_gate)
        {
            foreach (var (key, value) in _routes)
            {
                var name = channelNames != null && channelNames.TryGetValue(key.ChannelId, out var n) ? n : string.Empty;
                list.Add(new AudioRouteSetting
                {
                    ChannelId = key.ChannelId,
                    ChannelName = name,
                    TargetOutputKey = key.TargetKey,
                    Enabled = value.Enabled,
                    RouteGain = value.RouteGain
                });
            }
        }
        return list;
    }

    public void ImportSettings(IEnumerable<AudioRouteSetting>? settings)
    {
        if (settings == null) return;
        lock (_gate)
        {
            _routes.Clear();
            foreach (var s in settings)
            {
                if (s.ChannelId != Guid.Empty && !string.IsNullOrWhiteSpace(s.TargetOutputKey))
                {
                    _routes[(s.ChannelId, s.TargetOutputKey)] = (s.Enabled, Math.Clamp(s.RouteGain, 0f, 2f));
                }
            }
        }
        RoutesChanged?.Invoke();
    }
}
