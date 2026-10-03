using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Net.Security;
using System.Security.Authentication;
using AravalsStream.Core.RemoteCapture;
using AravalsStream.Core.Versioning;
using AravalsStream.Core.Services;

namespace AravalsStream.RemoteAgent;

internal sealed class RemotePairingServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Any, 45821);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate;
    private readonly Guid _deviceId;
    private readonly string _pairingCode;
    private readonly DateTimeOffset _expires = DateTimeOffset.UtcNow.AddMinutes(5);
    private readonly string _passphrase;
    private readonly Func<string, CancellationToken, Task<bool>> _revokeCredential;
    private readonly string[] _encoders;
    private readonly int _displayWidth, _displayHeight, _fps;
    private string _state = "Ready";
    private int _attempts;
    private int _paired;
    private int _credentialRevokedPending;
    private Task? _acceptLoop;
    public event Action<string>? StatusChanged;
    public event Action? CredentialRevoked;
    public string CertificateThumbprint => _certificate.Thumbprint;
    public RemoteAgentAdvertisement Advertisement => new(_deviceId, Environment.MachineName, AppVersion.Version,
        RemoteCaptureProtocol.CurrentProtocolVersion, 45821,
        [$"display:{_displayWidth}x{_displayHeight}:{_fps}"],
        _encoders, true, "srt", _displayWidth, _displayHeight, _fps, CertificateThumbprint, _state);

    public RemotePairingServer(Guid deviceId, string pairingCode, string passphrase,
        int displayWidth, int displayHeight, int fps, string[] encoders,
        Func<string, CancellationToken, Task<bool>> revokeCredential)
    {
        _deviceId = deviceId;
        _pairingCode = pairingCode;
        _passphrase = passphrase;
        _revokeCredential = revokeCredential;
        _displayWidth = displayWidth; _displayHeight = displayHeight; _fps = fps; _encoders = encoders;
        _certificate = RemoteAgentIdentity.LoadOrCreateCertificate(deviceId);
    }

    public void Start()
    {
        _listener.Start(4);
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public void SetState(string state) => _state = state is "Ready" or "Streaming" ? state : "Ready";

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                client.NoDelay = true;
                await HandleClientAsync(client, _stop.Token).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _credentialRevokedPending, 0) != 0)
                    CredentialRevoked?.Invoke();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) when (!_stop.IsCancellationRequested) { StatusChanged?.Invoke($"Pairing service error: {ex.Message}"); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var tls = new SslStream(client.GetStream(), false);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false
            }, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(tls, leaveOpen: true);
            using var writer = new StreamWriter(tls) { AutoFlush = true };
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null || line.Length > 1024) return;
            var request = JsonSerializer.Deserialize<RemotePairingRequest>(line);
            if (request?.DiscoverOnly == true)
            {
                var info = new RemotePairingResponse(true, _deviceId, Environment.MachineName, AppVersion.Version,
                    RemoteCaptureProtocol.CurrentProtocolVersion, null, CertificateThumbprint, Advertisement: Advertisement);
                await writer.WriteLineAsync(JsonSerializer.Serialize(info)).ConfigureAwait(false);
                return;
            }
            if (request?.Revoke == true)
            {
                if (request.DeviceId != _deviceId || !FixedEquals(_passphrase, request.CredentialProof))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new RemotePairingResponse(false, _deviceId,
                        Environment.MachineName, AppVersion.Version, RemoteCaptureProtocol.CurrentProtocolVersion,
                        null, CertificateThumbprint, "Credential revocation rejected."))).ConfigureAwait(false);
                    return;
                }
                // The paired flag is intentionally in-memory and resets when the Agent
                // restarts. Possession of the current credential proof must still allow
                // Forget to revoke that credential after a restart.
                var pairedState = Volatile.Read(ref _paired);
                while (pairedState != 2)
                {
                    var previous = Interlocked.CompareExchange(ref _paired, 2, pairedState);
                    if (previous == pairedState) break;
                    pairedState = previous;
                }
                if (pairedState == 2) return;
                var revoked = await _revokeCredential(request.CredentialProof!, cancellationToken).ConfigureAwait(false);
                if (!revoked)
                {
                    Volatile.Write(ref _paired, pairedState);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new RemotePairingResponse(false, _deviceId,
                        Environment.MachineName, AppVersion.Version, RemoteCaptureProtocol.CurrentProtocolVersion,
                        null, CertificateThumbprint, "Credential revocation failed."))).ConfigureAwait(false);
                    return;
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new RemotePairingResponse(true, _deviceId,
                    Environment.MachineName, AppVersion.Version, RemoteCaptureProtocol.CurrentProtocolVersion,
                    null, CertificateThumbprint))).ConfigureAwait(false);
                Volatile.Write(ref _credentialRevokedPending, 1);
                StatusChanged?.Invoke("Pairing credential revoked. Restarting with a new pairing key…");
                return;
            }
            if (Volatile.Read(ref _paired) != 0 || DateTimeOffset.UtcNow >= _expires || Interlocked.Increment(ref _attempts) > 5) return;
            if (request is null || request.DeviceId != _deviceId || !RemoteCaptureProtocol.ValidatePairingCode(request.PairingCode) ||
                !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(_pairingCode), System.Text.Encoding.ASCII.GetBytes(request.PairingCode ?? string.Empty)))
            {
                await writer.WriteLineAsync("{\"ok\":false,\"error\":\"Pairing rejected\"}").ConfigureAwait(false);
                return;
            }
            if (Interlocked.CompareExchange(ref _paired, 1, 0) != 0) return;
            var response = new RemotePairingResponse(true, _deviceId, Environment.MachineName,
                AppVersion.Version,
                RemoteCaptureProtocol.CurrentProtocolVersion, _passphrase, CertificateThumbprint);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
            StatusChanged?.Invoke("Paired. Waiting for Streaming PC media connection…");
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or JsonException or OperationCanceledException or CryptographicException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                var detail = ex.Message[..Math.Min(ex.Message.Length, 120)];
                AppLog.Write("RemotePairing", $"TLS/pairing connection failed ({ex.GetType().Name}): {detail}");
                StatusChanged?.Invoke($"Pairing connection failed ({ex.GetType().Name}): {detail}");
            }
        }
    }

    private static bool FixedEquals(string expected, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var candidateBytes = System.Text.Encoding.UTF8.GetBytes(candidate);
        return expectedBytes.Length == candidateBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null) try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _certificate.Dispose();
        _stop.Dispose();
    }
}
