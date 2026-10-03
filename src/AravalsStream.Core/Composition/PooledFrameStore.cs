namespace AravalsStream.Core.Composition;

// Three fixed buffers are enough for one writer and readers of the two previous frames.
// If every buffer is in use, real-time composition drops a frame instead of growing memory.
public sealed class PooledFrameStore
{
    internal sealed class Slot(int size)
    {
        public readonly byte[] Buffer = new byte[size];
        public int Readers;
        public bool Writing;
        public long Version;
    }

    private readonly object _gate = new();
    private readonly Slot[] _slots;
    private Slot? _latest;
    private long _drops;
    private long _nextVersion;

    public PooledFrameStore(int frameBytes, int capacity = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameBytes);
        if (capacity < 2 || capacity > 8) throw new ArgumentOutOfRangeException(nameof(capacity));
        _slots = Enumerable.Range(0, capacity).Select(_ => new Slot(frameBytes)).ToArray();
    }

    public int Capacity => _slots.Length;
    public long DroppedWrites => Interlocked.Read(ref _drops);

    public Writer? TryBeginWrite()
    {
        lock (_gate)
        {
            var slot = _slots.FirstOrDefault(s => !s.Writing && s.Readers == 0 && !ReferenceEquals(s, _latest));
            if (slot is null) { Interlocked.Increment(ref _drops); return null; }
            slot.Writing = true;
            return new Writer(this, slot);
        }
    }

    public Reader? AcquireLatest()
    {
        lock (_gate)
        {
            if (_latest is null) return null;
            _latest.Readers++;
            return new Reader(this, _latest);
        }
    }

    public sealed class Writer : IDisposable
    {
        private PooledFrameStore? _owner;
        private readonly Slot _slot;
        internal Writer(PooledFrameStore owner, Slot slot) { _owner = owner; _slot = slot; }
        public byte[] Buffer => _slot.Buffer;
        public void Publish()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._gate)
            {
                _slot.Version = ++owner._nextVersion;
                _slot.Writing = false;
                owner._latest = _slot;
            }
        }
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null) lock (owner._gate) _slot.Writing = false;
        }
    }

    public sealed class Reader : IDisposable
    {
        private PooledFrameStore? _owner;
        private readonly Slot _slot;
        internal Reader(PooledFrameStore owner, Slot slot) { _owner = owner; _slot = slot; }
        public byte[] Buffer => _slot.Buffer;
        public long Version => _slot.Version;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null) lock (owner._gate) _slot.Readers--;
        }
    }
}
