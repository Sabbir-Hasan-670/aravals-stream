namespace AravalsStream.Core.Composition;

/// <summary>
/// A reference-counted container for GPU or unmanaged resources that are shared
/// among multiple concurrent consumers (e.g. multiple stream destinations and recording).
/// Automatically disposes or reclaims the underlying resource when the reference count reaches zero.
/// </summary>
public sealed class ReferenceCountedResource<T> : IDisposable where T : class
{
    private readonly T _resource;
    private readonly Action<T> _onReclaim;
    private int _referenceCount;
    private bool _disposed;
    private int _ownerReleased;

    public T Resource => _resource;
    public int ReferenceCount => Volatile.Read(ref _referenceCount);
    public bool IsDisposed => _disposed;

    public ReferenceCountedResource(T resource, Action<T> onReclaim)
    {
        _resource = resource ?? throw new ArgumentNullException(nameof(resource));
        _onReclaim = onReclaim ?? throw new ArgumentNullException(nameof(onReclaim));
        _referenceCount = 1;
    }

    /// <summary>
    /// Acquires a new reference-counted lease.
    /// </summary>
    public ResourceLease<T> AcquireLease()
    {
        lock (this)
        {
            if (_disposed || Volatile.Read(ref _referenceCount) <= 0)
            {
                throw new ObjectDisposedException(nameof(ReferenceCountedResource<T>), "Cannot acquire lease on disposed resource.");
            }
            Interlocked.Increment(ref _referenceCount);
            return new ResourceLease<T>(this);
        }
    }

    internal void ReleaseLease()
    {
        if (Interlocked.Decrement(ref _referenceCount) == 0)
        {
            lock (this)
            {
                if (Volatile.Read(ref _referenceCount) != 0) return;
                if (_disposed) return;
                _disposed = true;
                _onReclaim(_resource);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
            ReleaseLease();
    }
}

/// <summary>
/// Scoped lease handle for a ReferenceCountedResource.
/// </summary>
public sealed class ResourceLease<T> : IDisposable where T : class
{
    private ReferenceCountedResource<T>? _parent;

    public T Resource => _parent?.Resource ?? throw new ObjectDisposedException(nameof(ResourceLease<T>));
    public bool IsDisposed => _parent is null;

    internal ResourceLease(ReferenceCountedResource<T> parent)
    {
        _parent = parent;
    }

    public void Dispose()
    {
        var parent = Interlocked.Exchange(ref _parent, null);
        parent?.ReleaseLease();
    }
}

/// <summary>
/// Bounded pool for GPU textures or surfaces to avoid allocation spikes and VRAM leaks.
/// </summary>
public sealed class BoundedResourcePool<TKey, TResource> : IDisposable 
    where TKey : notnull 
    where TResource : class, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<TKey, Queue<TResource>> _pool = new();
    private readonly int _maxCapacityPerKey;
    private bool _disposed;

    public int MaxCapacityPerKey => _maxCapacityPerKey;

    public BoundedResourcePool(int maxCapacityPerKey = 4)
    {
        _maxCapacityPerKey = Math.Max(1, maxCapacityPerKey);
    }

    public TResource? TryRent(TKey key)
    {
        lock (_lock)
        {
            if (_disposed) return null;
            if (_pool.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }
            return null;
        }
    }

    public bool Return(TKey key, TResource resource)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                resource.Dispose();
                return false;
            }

            if (!_pool.TryGetValue(key, out var queue))
            {
                queue = new Queue<TResource>();
                _pool[key] = queue;
            }

            if (queue.Count < _maxCapacityPerKey)
            {
                queue.Enqueue(resource);
                return true;
            }
            else
            {
                resource.Dispose();
                return false;
            }
        }
    }

    public int GetPooledCount(TKey key)
    {
        lock (_lock)
        {
            return _pool.TryGetValue(key, out var queue) ? queue.Count : 0;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var queue in _pool.Values)
            {
                while (queue.Count > 0)
                {
                    try { queue.Dequeue().Dispose(); } catch { }
                }
            }
            _pool.Clear();
        }
    }
}
