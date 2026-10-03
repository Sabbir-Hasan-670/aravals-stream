namespace AravalsStream.Core.RemoteCapture;

/// <summary>Runs the long-lived control plane separately from the explicitly started media plane.</summary>
public sealed class AgentLifecycleCoordinator(
    Func<Task<IAsyncDisposable>> startControlPlane,
    Func<Task<IAsyncDisposable>> startMediaPlane) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAsyncDisposable? _control;
    private IAsyncDisposable? _media;
    private bool _disposed;

    public bool ControlPlaneRunning => _control is not null;
    public bool MediaPlaneRunning => _media is not null;

    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_control is null) _control = await startControlPlane().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task StartMediaAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_control is null) throw new InvalidOperationException("Start the control plane before media.");
            if (_media is null) _media = await startMediaPlane().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task StopMediaAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var media = _media;
            _media = null;
            if (media is not null) await media.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            var media = _media; _media = null;
            var control = _control; _control = null;
            try
            {
                if (media is not null) await media.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (control is not null) await control.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }
}
