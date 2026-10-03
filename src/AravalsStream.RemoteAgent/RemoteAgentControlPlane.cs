using AravalsStream.Core.RemoteCapture;
using AravalsStream.Core.Services;

namespace AravalsStream.RemoteAgent;

/// <summary>Owns discovery and pairing for the lifetime of the Agent, independently of capture.</summary>
internal sealed class RemoteAgentControlPlane : IAsyncDisposable
{
    private readonly Guid _deviceId;
    private string _passphrase;
    private readonly RemoteAgentAdvertisement _baseAdvertisement;
    private RemotePairingServer? _pairing;
    private readonly RemoteDiscoveryAnnouncer _discovery = new();
    private bool _discoveryStarted;
    private int _disposed;
    private string _state = "Ready";

    public string PairingCode { get; private set; }
    public event Action<string>? StatusChanged;
    public event Action? CredentialRevoked;

    private RemoteAgentControlPlane(RemoteAgentIdentity identity, int width, int height, int fps, string[] encoders)
    {
        _deviceId = identity.DeviceId;
        _passphrase = identity.SrtPassphrase;
        PairingCode = RemoteCaptureProtocol.CreatePairingCode();
        _baseAdvertisement = new RemoteAgentAdvertisement(identity.DeviceId, Environment.MachineName,
            AravalsStream.Core.Versioning.AppVersion.Version, RemoteCaptureProtocol.CurrentProtocolVersion,
            45821, [$"display:{width}x{height}:{fps}"], encoders, true, "srt", width, height, fps);
    }

    public static async Task<RemoteAgentControlPlane> StartAsync(int width, int height, int fps, string[] encoders)
    {
        var plane = new RemoteAgentControlPlane(RemoteAgentIdentity.LoadOrCreate(), width, height, fps, encoders);
        try
        {
            plane.StartListeners();
            await Task.CompletedTask;
            return plane;
        }
        catch
        {
            await plane.DisposeAsync();
            throw;
        }
    }

    public void SetStreaming(bool streaming)
    {
        _state = streaming ? "Streaming" : "Ready";
        _pairing?.SetState(_state);
        if (_pairing is not null)
            _discovery.Update(_pairing.Advertisement);
    }

    private void StartListeners()
    {
        _pairing = new RemotePairingServer(_deviceId, PairingCode, _passphrase,
            _baseAdvertisement.DisplayWidth, _baseAdvertisement.DisplayHeight, _baseAdvertisement.CaptureFps,
            _baseAdvertisement.Encoders.ToArray(), RevokeCredentialAsync);
        _pairing.StatusChanged += text => { AppLog.Write("RemoteControl", text); StatusChanged?.Invoke(text); };
        _pairing.CredentialRevoked += () => CredentialRevoked?.Invoke();
        _pairing.Start();
        if (_discoveryStarted)
            _discovery.Update(_pairing.Advertisement);
        else
        {
            _discovery.Start(_pairing.Advertisement);
            _discoveryStarted = true;
        }
        AppLog.Write("RemoteControl", $"ControlPlaneStarted device={_deviceId:N}, pairingPort=45821, discoveryPort=45819, state={_state}");
        StatusChanged?.Invoke("Ready — pairing and discovery active.");
    }

    private Task<bool> RevokeCredentialAsync(string proof, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RemoteAgentIdentity.TryRotatePassphrase(_deviceId, proof));
    }

    public async Task RefreshPairingAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var old = _pairing;
        _pairing = null;
        if (old is not null) await old.DisposeAsync();
        _passphrase = RemoteAgentIdentity.LoadOrCreate().SrtPassphrase;
        PairingCode = RemoteCaptureProtocol.CreatePairingCode();
        StartListeners();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_pairing is not null) await _pairing.DisposeAsync();
        await _discovery.DisposeAsync();
        AppLog.Write("RemoteControl", "ControlPlaneStopped; pairing and discovery disposed.");
    }
}
