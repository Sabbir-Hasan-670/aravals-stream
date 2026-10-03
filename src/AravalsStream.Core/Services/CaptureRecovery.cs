namespace AravalsStream.Core.Services;

public sealed class CaptureRecovery
{
    private static readonly int[] Delays = [1, 2, 5, 10, 15, 30];
    private readonly Dictionary<Guid, (int Attempts, DateTimeOffset Next)> _failed = [];

    public void Failed(Guid id, DateTimeOffset now)
    {
        var attempts = _failed.TryGetValue(id, out var entry) ? entry.Attempts : 0;
        _failed[id] = (attempts + 1, now.AddSeconds(Delays[Math.Min(attempts, Delays.Length - 1)]));
    }

    public void Recovered(Guid id) => _failed.Remove(id);
    public bool IsFailed(Guid id) => _failed.ContainsKey(id);
    public IReadOnlyList<Guid> Due(DateTimeOffset now) => _failed.Where(p => p.Value.Next <= now).Select(p => p.Key).ToList();
    public void DeviceChanged(DateTimeOffset now)
    {
        foreach (var id in _failed.Keys.ToArray())
        {
            var entry = _failed[id];
            _failed[id] = (entry.Attempts, now);
        }
    }
    public void Remove(Guid id) => _failed.Remove(id);
}
