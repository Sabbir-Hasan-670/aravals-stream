namespace AravalsStream.Core.Recording;

public sealed class BoundedFrameQueue<T>
{
    private readonly Queue<T> _queue = new();
    private readonly object _gate = new();
    private readonly int _maxCapacity;
    private long _droppedCount;
    private long _enqueuedCount;
    private long _dequeuedCount;

    public int MaxCapacity => _maxCapacity;
    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    public long EnqueuedCount => Interlocked.Read(ref _enqueuedCount);
    public long DequeuedCount => Interlocked.Read(ref _dequeuedCount);
    public int Count { get { lock (_gate) return _queue.Count; } }

    public BoundedFrameQueue(int maxCapacity = 2)
    {
        _maxCapacity = Math.Max(1, maxCapacity);
    }

    public bool Enqueue(T item)
    {
        T? droppedItem = default;
        bool dropped = false;

        lock (_gate)
        {
            if (_queue.Count >= _maxCapacity)
            {
                droppedItem = _queue.Dequeue();
                dropped = true;
                Interlocked.Increment(ref _droppedCount);
            }

            _queue.Enqueue(item);
            Interlocked.Increment(ref _enqueuedCount);
        }

        if (dropped && droppedItem is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { }
        }

        return !dropped;
    }

    public bool TryDequeue(out T? item)
    {
        lock (_gate)
        {
            if (_queue.Count > 0)
            {
                item = _queue.Dequeue();
                Interlocked.Increment(ref _dequeuedCount);
                return true;
            }
        }

        item = default;
        return false;
    }

    public void Clear()
    {
        lock (_gate)
        {
            while (_queue.Count > 0)
            {
                var item = _queue.Dequeue();
                if (item is IDisposable disposable)
                {
                    try { disposable.Dispose(); } catch { }
                }
            }
        }
    }
}
