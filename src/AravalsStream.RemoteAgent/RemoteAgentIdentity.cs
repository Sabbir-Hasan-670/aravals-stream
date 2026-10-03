using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AravalsStream.Core.Services;

namespace AravalsStream.RemoteAgent;

internal sealed record RemoteAgentIdentity(Guid DeviceId, string SrtPassphrase)
{
    private static readonly object Gate = new();

    public static RemoteAgentIdentity LoadOrCreate()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Remote Agent pairing storage requires Windows DPAPI.");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AravalsStream", "RemoteAgent");
        Directory.CreateDirectory(folder);
        var idPath = Path.Combine(folder, "device-id");
        if (!Guid.TryParse(File.Exists(idPath) ? File.ReadAllText(idPath) : null, out var id) || id == Guid.Empty)
        {
            id = Guid.NewGuid();
            File.WriteAllText(idPath, id.ToString("D"));
        }
        var storage = new DpapiSecretStorage(Path.Combine(folder, "secrets"));
        var secret = storage.Get("remote-srt-passphrase");
        if (secret is null)
        {
            secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            storage.Set("remote-srt-passphrase", secret);
        }
        return new RemoteAgentIdentity(id, secret);
    }

    public static bool TryRotatePassphrase(Guid deviceId, string expectedPassphrase)
    {
        lock (Gate)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AravalsStream", "RemoteAgent");
            var idPath = Path.Combine(folder, "device-id");
            if (!Guid.TryParse(File.Exists(idPath) ? File.ReadAllText(idPath) : null, out var storedId) || storedId != deviceId)
                return false;
            var storage = new DpapiSecretStorage(Path.Combine(folder, "secrets"));
            var current = storage.Get("remote-srt-passphrase");
            if (string.IsNullOrEmpty(current) || !FixedEquals(current, expectedPassphrase)) return false;
            var replacement = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            storage.Set("remote-srt-passphrase", replacement);
            return true;
        }
    }

    public static X509Certificate2 LoadOrCreateCertificate(Guid deviceId)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AravalsStream", "RemoteAgent");
        Directory.CreateDirectory(folder);
        var storage = new DpapiSecretStorage(Path.Combine(folder, "secrets"));
        var persisted = storage.Get("remote-agent-certificate");
        if (!string.IsNullOrWhiteSpace(persisted))
        {
            try
            {
                return new X509Certificate2(Convert.FromBase64String(persisted), (string?)null,
                    X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            }
            catch (CryptographicException) { storage.Delete("remote-agent-certificate"); }
            catch (FormatException) { storage.Delete("remote-agent-certificate"); }
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Aravals Remote Capture {deviceId:N}", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        var serverAuth = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(serverAuth, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var pfx = generated.Export(X509ContentType.Pfx);
        storage.Set("remote-agent-certificate", Convert.ToBase64String(pfx));
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
