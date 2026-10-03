using System.Text.Json;
using System.Security.Authentication;
using AravalsStream.Core.Interfaces;

namespace AravalsStream.Core.RemoteCapture;

public sealed record PairedRemoteDevice(Guid DeviceId, string DisplayName, string Host, int ControlPort,
    int ProtocolVersion, string AgentVersion, string CertificateThumbprint, string SecretReference, DateTimeOffset LastSeen,
    IReadOnlyList<string>? CaptureModes = null, IReadOnlyList<string>? Encoders = null, string State = "Ready");

/// <summary>Persists public device metadata in JSON and keeps the connection passphrase in platform secret storage.</summary>
public sealed class PairedDeviceRegistry(ISecretStorage secrets, string? metadataPath = null)
{
    private readonly string _path = metadataPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AravalsStream", "remote-devices.json");

    public async Task<IReadOnlyList<PairedRemoteDevice>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<PairedRemoteDevice>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task SaveAsync(PairedRemoteDevice device, string passphrase, CancellationToken cancellationToken = default)
    {
        await secrets.StoreAsync(device.SecretReference, passphrase, cancellationToken).ConfigureAwait(false);
        var devices = (await ListAsync(cancellationToken).ConfigureAwait(false)).Where(d => d.DeviceId != device.DeviceId).Append(device).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, devices, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetPassphraseAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var device = (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.DeviceId == deviceId);
        return device is null ? null : await secrets.GetAsync(device.SecretReference, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PairedRemoteDevice> RefreshMetadataAsync(Guid deviceId, string host,
        RemoteAgentAdvertisement advertisement, CancellationToken cancellationToken = default)
    {
        var devices = (await ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var index = devices.FindIndex(d => d.DeviceId == deviceId);
        if (index < 0) throw new InvalidOperationException("The device is not paired.");
        var saved = devices[index];
        if (advertisement.DeviceId != saved.DeviceId || advertisement.ProtocolVersion != saved.ProtocolVersion ||
            string.IsNullOrWhiteSpace(saved.CertificateThumbprint) ||
            !string.Equals(saved.CertificateThumbprint, advertisement.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new AuthenticationException("Agent identity, protocol, or certificate changed. Forget and pair the device again to confirm the change.");
        var refreshed = saved with
        {
            DisplayName = advertisement.ComputerName,
            Host = host,
            ControlPort = advertisement.ControlPort,
            AgentVersion = advertisement.AgentVersion,
            CaptureModes = advertisement.CaptureModes,
            Encoders = advertisement.Encoders,
            State = advertisement.State,
            LastSeen = DateTimeOffset.UtcNow
        };
        devices[index] = refreshed;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, devices, cancellationToken: cancellationToken).ConfigureAwait(false);
        return refreshed;
    }

    public async Task<bool> ForgetAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var devices = (await ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var match = devices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (match is null) return false;
        var remotelyRevoked = false;
        var passphrase = await secrets.GetAsync(match.SecretReference, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(passphrase))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await RemotePairingClient.RevokeAsync(match.Host, match.ControlPort, match.DeviceId,
                    match.CertificateThumbprint, passphrase, timeout.Token).ConfigureAwait(false);
                remotelyRevoked = true;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or
                                       AuthenticationException or OperationCanceledException or TimeoutException)
            {
                // Forget must still remove this Desktop's local metadata and credential when the Agent is offline.
            }
        }
        await secrets.RemoveAsync(match.SecretReference, cancellationToken).ConfigureAwait(false);
        devices.Remove(match);
        if (devices.Count == 0) { File.Delete(_path); return remotelyRevoked; }
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, devices, cancellationToken: cancellationToken).ConfigureAwait(false);
        return remotelyRevoked;
    }
}
