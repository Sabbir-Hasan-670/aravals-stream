namespace AravalsStream.Core.Services;

public sealed class CaptureRecovery
{
    private static readonly int[] Delays = [1, 2, 5, 10, 15, 30];
    private readonly Dictionary<Guid, (int Attempts, DateTimeOffset Next, bool RetrySoon)> _failed = [];

    public void Failed(Guid id, DateTimeOffset now, bool retrySoon = false)
    {
        var attempts = _failed.TryGetValue(id, out var entry) ? entry.Attempts : 0;
        var delaySeconds = retrySoon ? 2 : Delays[Math.Min(attempts, Delays.Length - 1)];
        _failed[id] = (attempts + 1, now.AddSeconds(delaySeconds), retrySoon);
    }

    public void Defer(Guid id, DateTimeOffset now)
    {
        if (!_failed.TryGetValue(id, out var entry)) return;
        var delayIndex = Math.Max(0, entry.Attempts - 1);
        var delaySeconds = entry.RetrySoon ? 2 : Delays[Math.Min(delayIndex, Delays.Length - 1)];
        _failed[id] = (entry.Attempts, now.AddSeconds(delaySeconds), entry.RetrySoon);
    }

    public void Recovered(Guid id) => _failed.Remove(id);
    public bool IsFailed(Guid id) => _failed.ContainsKey(id);
    public IReadOnlyList<Guid> Due(DateTimeOffset now) => _failed.Where(p => p.Value.Next <= now).Select(p => p.Key).ToList();
    public void DeviceChanged(DateTimeOffset now)
    {
        foreach (var id in _failed.Keys.ToArray())
        {
            var entry = _failed[id];
            _failed[id] = (entry.Attempts, now, entry.RetrySoon);
        }
    }
    public void Remove(Guid id) => _failed.Remove(id);
}
