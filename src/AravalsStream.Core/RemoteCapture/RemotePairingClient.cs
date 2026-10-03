using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace AravalsStream.Core.RemoteCapture;

public sealed record RemotePairingRequest(Guid DeviceId, string? PairingCode, bool DiscoverOnly = false,
    bool Revoke = false, string? CredentialProof = null);
public sealed record RemotePairingResponse(bool Ok, Guid DeviceId, string ComputerName, string AgentVersion,
    int ProtocolVersion, string? Passphrase, string? CertificateThumbprint, string? Error = null,
    RemoteAgentAdvertisement? Advertisement = null);

public static class RemotePairingClient
{
    public static async Task RevokeAsync(string host, int port, Guid deviceId, string expectedCertificateThumbprint,
        string credentialProof, CancellationToken cancellationToken = default)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var observedThumbprint = (string?)null;
        await using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert is null) return false;
            using var certificate = new X509Certificate2(cert);
            observedThumbprint = certificate.Thumbprint;
            return string.Equals(expectedCertificateThumbprint, observedThumbprint, StringComparison.OrdinalIgnoreCase);
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        { TargetHost = "Aravals Remote Capture", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expectedCertificateThumbprint, observedThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new AuthenticationException("Agent certificate fingerprint did not match the paired device.");
        using var reader = new StreamReader(tls, leaveOpen: true);
        using var writer = new StreamWriter(tls) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new RemotePairingRequest(deviceId, null,
            Revoke: true, CredentialProof: credentialProof))).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null || line.Length > 2048) throw new IOException("Agent closed the credential revocation connection.");
        var response = JsonSerializer.Deserialize<RemotePairingResponse>(line) ?? throw new IOException("Invalid Agent revocation response.");
        if (!response.Ok || response.DeviceId != deviceId)
            throw new AuthenticationException(response.Error ?? "Agent rejected credential revocation.");
    }

    public static async Task<RemotePairingResponse> PairAsync(string host, int port, Guid deviceId, string code,
        string? expectedCertificateThumbprint, CancellationToken cancellationToken = default)
    {
        if (!RemoteCaptureProtocol.ValidatePairingCode(code)) throw new ArgumentException("Enter the six-digit pairing code shown on the Agent.", nameof(code));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var observedThumbprint = (string?)null;
        await using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert is null) return false;
            using var certificate = new X509Certificate2(cert);
            observedThumbprint = certificate.Thumbprint;
            return string.IsNullOrWhiteSpace(expectedCertificateThumbprint) ||
                string.Equals(expectedCertificateThumbprint, observedThumbprint, StringComparison.OrdinalIgnoreCase);
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        { TargetHost = "Aravals Remote Capture", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(tls, leaveOpen: true);
        using var writer = new StreamWriter(tls) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new RemotePairingRequest(deviceId, code))).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null || line.Length > 2048) throw new IOException("Agent closed pairing connection.");
        var response = JsonSerializer.Deserialize<RemotePairingResponse>(line) ?? throw new IOException("Invalid Agent pairing response.");
        if (!response.Ok) throw new IOException(response.Error ?? "Agent rejected pairing.");
        if (response.DeviceId != deviceId || !RemoteCaptureProtocol.IsCompatible(response.ProtocolVersion))
            throw new IOException("Remote Agent protocol version is incompatible.");
        if (!string.Equals(response.CertificateThumbprint, observedThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new AuthenticationException("Agent certificate fingerprint did not match the pairing response.");
        return response;
    }

    public static async Task<RemoteAgentAdvertisement> DescribeAsync(string host, int port = 45821,
        CancellationToken cancellationToken = default, string? expectedCertificateThumbprint = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        string? observedThumbprint = null;
        await using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert is null) return false;
            using var observed = new X509Certificate2(cert);
            observedThumbprint = observed.Thumbprint;
            return string.IsNullOrWhiteSpace(expectedCertificateThumbprint) ||
                string.Equals(expectedCertificateThumbprint, observedThumbprint, StringComparison.OrdinalIgnoreCase);
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        { TargetHost = "Aravals Remote Capture", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(tls, leaveOpen: true);
        using var writer = new StreamWriter(tls) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new RemotePairingRequest(Guid.Empty, null, true))).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null || line.Length > 4096) throw new IOException("Agent closed the information request.");
        var response = JsonSerializer.Deserialize<RemotePairingResponse>(line) ?? throw new IOException("Invalid Agent information response.");
        var advertisement = response.Advertisement ?? throw new IOException(response.Error ?? "Agent did not return capture information.");
        if (!string.Equals(advertisement.CertificateThumbprint, observedThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new AuthenticationException("Agent certificate fingerprint did not match its discovery response.");
        return advertisement;
    }
}
