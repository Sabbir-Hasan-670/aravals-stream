using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AravalsStream.Core.RemoteCapture;

public sealed record RemoteAgentAdvertisement(
    Guid DeviceId,
    string ComputerName,
    string AgentVersion,
    int ProtocolVersion,
    int ControlPort,
    IReadOnlyList<string> CaptureModes,
    IReadOnlyList<string> Encoders,
    bool Available = true,
    string Transport = "srt",
    int DisplayWidth = 0,
    int DisplayHeight = 0,
    int CaptureFps = 60,
    string? CertificateThumbprint = null,
    string State = "Ready");

public enum RemoteLatencyMode { UltraLow, Balanced, Stable }

public sealed record SrtConnectionOptions(string Host, int Port, int LatencyMilliseconds, string Passphrase)
{
    public Uri ToUri()
    {
        if (!IPAddress.TryParse(Host, out _) && (Host.Length is 0 or > 253 || Host.Any(char.IsWhiteSpace)))
            throw new ArgumentException("Host must be an IP address or hostname.", nameof(Host));
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (LatencyMilliseconds is < 20 or > 4000) throw new ArgumentOutOfRangeException(nameof(LatencyMilliseconds));
        if (Passphrase.Length is < 10 or > 79 || Passphrase.Any(char.IsControl))
            throw new ArgumentException("SRT passphrase must contain 10 to 79 printable characters.", nameof(Passphrase));
        var builder = new UriBuilder("srt", Host, Port)
        {
            Query = $"mode=caller&latency={LatencyMilliseconds}&passphrase={Uri.EscapeDataString(Passphrase)}&pbkeylen=16"
        };
        return builder.Uri;
    }

    public override string ToString() => RemoteCaptureProtocol.Redact(ToUri().ToString());
}

public static class RemoteCaptureProtocol
{
    public const int CurrentProtocolVersion = 1;
    public const int DiscoveryPort = 45819;
    public const int DefaultTransportPort = 45820;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string CreatePairingCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static bool ValidatePairingCode(string? value) => value is { Length: 6 } && value.All(char.IsAsciiDigit);

    public static bool IsCompatible(int protocolVersion) => protocolVersion == CurrentProtocolVersion;

    public static int LatencyFor(RemoteLatencyMode mode) => mode switch
    {
        RemoteLatencyMode.UltraLow => 80,
        RemoteLatencyMode.Balanced => 250,
        RemoteLatencyMode.Stable => 600,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static string SelectH264Encoder(IEnumerable<string> available)
    {
        var set = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        foreach (var encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" })
            if (set.Contains(encoder)) return encoder;
        return "libx264";
    }

    public static byte[] SerializeAdvertisement(RemoteAgentAdvertisement advertisement)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        if (advertisement.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(advertisement.ComputerName) ||
            advertisement.ComputerName.Length > 128 || advertisement.ControlPort is < 1 or > 65535)
            throw new ArgumentException("Agent advertisement is invalid.", nameof(advertisement));
        if (advertisement.ProtocolVersion <= 0) throw new ArgumentOutOfRangeException(nameof(advertisement));
        return JsonSerializer.SerializeToUtf8Bytes(advertisement, JsonOptions);
    }

    public static bool TryParseAdvertisement(ReadOnlySpan<byte> payload, out RemoteAgentAdvertisement? advertisement)
    {
        advertisement = null;
        if (payload.Length is 0 or > 4096) return false;
        try
        {
            var candidate = JsonSerializer.Deserialize<RemoteAgentAdvertisement>(payload, JsonOptions);
            if (candidate is null || candidate.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(candidate.ComputerName) ||
                candidate.ComputerName.Length > 128 || candidate.ProtocolVersion <= 0 || candidate.ControlPort is < 1 or > 65535)
                return false;
            advertisement = candidate;
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Redact SRT URL credentials while retaining the address and non-secret options.
        return System.Text.RegularExpressions.Regex.Replace(text,
            @"(?i)(srt://[^\s?]+\?[^\s]*passphrase=)[^&\s]+",
            "$1[REDACTED]");
    }
}
