using AravalsStream.Core.Models;

namespace AravalsStream.Core.Alerts;

public sealed class AlertInstance
{
    public required StreamEvent Event { get; init; }
    public required AlertDefinition Definition { get; init; }
    public int GroupedCount { get; set; } = 1;
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StartedAt { get; set; }
    public string Title => AlertTemplate.Render(Definition.TitleTemplate, Event, GroupedCount);
    public string Message => GroupedCount > 1 && Event.EventType == StreamEventType.Follow
        ? $"{Event.ActorName} and {GroupedCount - 1} others followed!"
        : AlertTemplate.Render(Definition.MessageTemplate, Event, GroupedCount);
    public TimeSpan TotalDuration => TimeSpan.FromMilliseconds(Math.Max(0, Definition.EnterMilliseconds) +
        Math.Max(0, Definition.DisplayMilliseconds) + Math.Max(0, Definition.ExitMilliseconds));
    public double OpacityAt(DateTimeOffset now)
    {
        var elapsed = (now - StartedAt).TotalMilliseconds;
        if (elapsed < 0 || elapsed >= TotalDuration.TotalMilliseconds) return 0;
        if (Definition.Animation == AlertAnimation.None) return 1;
        if (Definition.EnterMilliseconds > 0 && elapsed < Definition.EnterMilliseconds)
            return Math.Clamp(elapsed / Definition.EnterMilliseconds, 0, 1);
        var exitStart = Definition.EnterMilliseconds + Definition.DisplayMilliseconds;
        if (Definition.ExitMilliseconds > 0 && elapsed >= exitStart)
            return Math.Clamp((TotalDuration.TotalMilliseconds - elapsed) / Definition.ExitMilliseconds, 0, 1);
        return 1;
    }
}

public sealed class AlertEngine
{
    private readonly AlertSettings _settings;
    private readonly List<AlertInstance> _queue = [];
    private readonly Dictionary<string, DateTimeOffset> _cooldown = new(StringComparer.Ordinal);
    private readonly Queue<string> _recent = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
    public AlertInstance? Current { get; private set; }
    public event Action<AlertInstance>? Activated;
    public int QueuedCount { get { lock (_gate) return _queue.Count; } }
    public AlertEngine(AlertSettings settings) => _settings = settings;

    public bool Enqueue(StreamEvent item, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_seen.Add(item.Platform + ":" + item.Id)) return false;
            _recent.Enqueue(item.Platform + ":" + item.Id);
            while (_recent.Count > 2048) _seen.Remove(_recent.Dequeue());
            var definition = _settings.Definitions.FirstOrDefault(d => d.EventType == item.EventType && d.Enabled);
            if (definition == null) return false;
            var cooldownKey = item.Platform + ":" + item.EventType + ":" + item.ActorId;
            if (!item.IsSimulated && _cooldown.TryGetValue(cooldownKey, out var previous) &&
                now - previous < TimeSpan.FromMilliseconds(Math.Max(0, _settings.CooldownMilliseconds))) return false;
            _cooldown[cooldownKey] = now;
            if (_cooldown.Count > 2048) _cooldown.Clear();
            if (_settings.GroupRepeatedEvents && item.EventType == StreamEventType.Follow)
            {
                var group = _queue.LastOrDefault(a => a.Event.EventType == item.EventType &&
                    a.Event.Platform == item.Platform &&
                    now - a.EnqueuedAt <= TimeSpan.FromMilliseconds(_settings.GroupWindowMilliseconds));
                if (group != null) { group.GroupedCount++; return true; }
            }
            if (_queue.Count >= Math.Clamp(_settings.MaxQueueLength, 1, 100)) return false;
            _queue.Add(new AlertInstance { Event = item, Definition = definition.Copy(), EnqueuedAt = now });
            return true;
        }
    }

    public AlertInstance? Tick(DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        AlertInstance? activated = null;
        lock (_gate)
        {
            if (Current != null && now - Current.StartedAt >= Current.TotalDuration)
            {
                Current = null;
                _nextAllowed = now.AddMilliseconds(Math.Max(0, _settings.GapMilliseconds));
            }
            if (Current == null && now >= _nextAllowed && _queue.Count > 0)
            {
                var chosen = _queue.OrderByDescending(a => Priority(a.Event.EventType))
                    .ThenBy(a => a.EnqueuedAt).First();
                _queue.Remove(chosen);
                chosen.StartedAt = now;
                Current = activated = chosen;
            }
        }
        if (activated != null) try { Activated?.Invoke(activated); } catch { }
        return Current;
    }

    private static int Priority(StreamEventType type) => type switch
    {
        StreamEventType.PaidMessage or StreamEventType.Donation => 4,
        StreamEventType.GiftSubscription or StreamEventType.Subscription or StreamEventType.Membership => 3,
        StreamEventType.Raid or StreamEventType.Cheer => 2,
        StreamEventType.Follow => 1,
        _ => 0
    };
}
