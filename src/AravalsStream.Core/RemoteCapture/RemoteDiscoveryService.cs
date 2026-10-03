using System.Net;
using System.Net.Sockets;

namespace AravalsStream.Core.RemoteCapture;

/// <summary>LAN-only broadcast discovery. Advertisements contain public capabilities only.</summary>
public sealed class RemoteDiscoveryService : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly int _port;

    public event Action<RemoteAgentAdvertisement, IPEndPoint>? AgentDiscovered;

    public RemoteDiscoveryService(int port = RemoteCaptureProtocol.DiscoveryPort)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.EnableBroadcast = true;
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    public Task StartAsync(RemoteAgentAdvertisement? advertisement = null, CancellationToken cancellationToken = default)
    {
        if (advertisement is not null) _ = BroadcastLoopAsync(advertisement, cancellationToken);
        return ListenLoopAsync(cancellationToken);
    }

    private async Task BroadcastLoopAsync(RemoteAgentAdvertisement advertisement, CancellationToken externalToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, externalToken);
        var payload = RemoteCaptureProtocol.SerializeAdvertisement(advertisement);
        var endpoint = new IPEndPoint(IPAddress.Broadcast, _port);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await _udp.SendAsync(payload, endpoint, linked.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ListenLoopAsync(CancellationToken externalToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, externalToken);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var packet = await _udp.ReceiveAsync(linked.Token).ConfigureAwait(false);
                if (!RemoteCaptureProtocol.TryParseAdvertisement(packet.Buffer, out var ad) || ad is null) continue;
                AgentDiscovered?.Invoke(ad, packet.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp.Dispose();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class RemoteDiscoveryAnnouncer : IAsyncDisposable
{
    private readonly UdpClient _udp = new(AddressFamily.InterNetwork) { EnableBroadcast = true };
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private readonly int _port;
    private readonly object _advertisementGate = new();
    private byte[]? _packet;

    public RemoteDiscoveryAnnouncer(int port = RemoteCaptureProtocol.DiscoveryPort)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
    }

    public void Start(RemoteAgentAdvertisement advertisement)
    {
        if (_loop is not null) throw new InvalidOperationException("Discovery announcer has already started.");
        lock (_advertisementGate)
        {
            if (_loop is not null) throw new InvalidOperationException("Discovery announcer has already started.");
            _packet = RemoteCaptureProtocol.SerializeAdvertisement(advertisement);
            _loop = Task.Run(RunAsync);
        }
    }

    public void Update(RemoteAgentAdvertisement advertisement)
    {
        var packet = RemoteCaptureProtocol.SerializeAdvertisement(advertisement);
        lock (_advertisementGate)
        {
            if (_loop is null) throw new InvalidOperationException("Discovery announcer has not started.");
            _packet = packet;
        }
    }

    private async Task RunAsync()
    {
        var endpoint = new IPEndPoint(IPAddress.Broadcast, _port);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                byte[]? packet;
                lock (_advertisementGate) packet = _packet;
                if (packet is not null) await _udp.SendAsync(packet, endpoint, _stop.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _udp.Dispose();
        if (_loop is not null) try { await _loop.ConfigureAwait(false); } catch { }
        _stop.Dispose();
    }
}
