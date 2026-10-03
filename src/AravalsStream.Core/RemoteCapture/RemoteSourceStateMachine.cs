namespace AravalsStream.Core.RemoteCapture;

public enum RemoteSourceState { Offline, Discovered, Pairing, Connecting, Live, NetworkUnstable, Reconnecting, Error }

public sealed class RemoteSourceStateMachine
{
    private static readonly int[] RetryDelaysSeconds = [1, 2, 4, 8, 15, 30];
    private int _attempt;
    public RemoteSourceState State { get; private set; } = RemoteSourceState.Offline;
    public int ReconnectCount { get; private set; }
    public DateTimeOffset? RetryAt { get; private set; }

    public void Transition(RemoteSourceState next, DateTimeOffset? now = null)
    {
        if (!Allowed(State, next)) throw new InvalidOperationException($"Invalid remote source state transition: {State} → {next}.");
        State = next;
        if (next == RemoteSourceState.Live) { _attempt = 0; RetryAt = null; }
        if (next == RemoteSourceState.Reconnecting)
        {
            ReconnectCount++;
            var delay = RetryDelaysSeconds[Math.Min(_attempt++, RetryDelaysSeconds.Length - 1)];
            RetryAt = (now ?? DateTimeOffset.UtcNow).AddSeconds(delay);
        }
    }

    public bool IsRetryDue(DateTimeOffset now) => State == RemoteSourceState.Reconnecting && RetryAt <= now;

    private static bool Allowed(RemoteSourceState from, RemoteSourceState to) => from == to || (from, to) switch
    {
        (RemoteSourceState.Offline, RemoteSourceState.Discovered or RemoteSourceState.Pairing or RemoteSourceState.Connecting or RemoteSourceState.Error) => true,
        (RemoteSourceState.Discovered, RemoteSourceState.Offline or RemoteSourceState.Pairing or RemoteSourceState.Connecting or RemoteSourceState.Error) => true,
        (RemoteSourceState.Pairing, RemoteSourceState.Offline or RemoteSourceState.Discovered or RemoteSourceState.Connecting or RemoteSourceState.Error) => true,
        (RemoteSourceState.Connecting, RemoteSourceState.Live or RemoteSourceState.NetworkUnstable or RemoteSourceState.Reconnecting or RemoteSourceState.Error or RemoteSourceState.Offline) => true,
        (RemoteSourceState.Live, RemoteSourceState.NetworkUnstable or RemoteSourceState.Reconnecting or RemoteSourceState.Error or RemoteSourceState.Offline) => true,
        (RemoteSourceState.NetworkUnstable, RemoteSourceState.Live or RemoteSourceState.Reconnecting or RemoteSourceState.Error or RemoteSourceState.Offline) => true,
        (RemoteSourceState.Reconnecting, RemoteSourceState.Connecting or RemoteSourceState.Live or RemoteSourceState.NetworkUnstable or RemoteSourceState.Error or RemoteSourceState.Offline) => true,
        (RemoteSourceState.Error, RemoteSourceState.Discovered or RemoteSourceState.Connecting or RemoteSourceState.Pairing or RemoteSourceState.Offline) => true,
        _ => false
    };
}

/// <summary>A single-slot frame handoff that always replaces stale video with the newest frame.</summary>
public sealed class LatestFrameSlot<T> where T : class
{
    private readonly object _gate = new();
    private T? _value;
    private readonly Action<T>? _dispose;
    public long ReplacedCount { get; private set; }
    public LatestFrameSlot(Action<T>? dispose = null) => _dispose = dispose;

    public void Publish(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        T? stale;
        lock (_gate) { stale = _value; _value = value; if (stale is not null) ReplacedCount++; }
        if (stale is not null) _dispose?.Invoke(stale);
    }

    public T? Take()
    {
        lock (_gate) { var current = _value; _value = null; return current; }
    }

    public void Clear()
    {
        var stale = Take();
        if (stale is not null) _dispose?.Invoke(stale);
    }
}
