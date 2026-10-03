using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AravalsStream.Relay;

public static class RelayPreflight
{
    public static bool PublicHttpsUrl(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) || candidate.Scheme != "https" ||
            candidate.IsLoopback || !string.IsNullOrEmpty(candidate.UserInfo) || !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment) || candidate.AbsolutePath != "/" ||
            candidate.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) ||
            candidate.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || !candidate.Host.Contains('.')) return false;
        if (IPAddress.TryParse(candidate.Host, out var address))
        {
            var b = address.GetAddressBytes();
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || b[0] is 0 or 10 or 127 ||
                (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
                (b[0] == 169 && b[1] == 254)) return false;
        }
        uri = candidate; return true;
    }

    public static string? SigningKeyError(string? value)
    {
        try
        {
            var key = Convert.FromBase64String(value ?? "");
            var decoded = Encoding.UTF8.GetString(key);
            if (key.Length < 32 || key.Distinct().Count() < 16 || Placeholder(decoded) ||
                key.All(b => b is >= 32 and <= 126) ||
                (key.Length > 1 && key.Skip(1).Select((b, i) => (b - key[i] + 256) % 256).Distinct().Count() == 1))
                return "RELAY_TOKEN_SIGNING_KEY must contain at least 32 securely generated random bytes (Base64).";
            return null;
        }
        catch (FormatException) { return "RELAY_TOKEN_SIGNING_KEY must be a securely generated Base64 key."; }
    }

    public static IReadOnlyList<string> Validate(IConfiguration config, bool development, bool checkStorage = true)
    {
        var errors = new List<string>();
        if (SigningKeyError(config["RELAY_TOKEN_SIGNING_KEY"]) is { } keyError) errors.Add(keyError);
        if (!development && string.IsNullOrWhiteSpace(config["RELAY_ALLOWED_HOSTS"])) errors.Add("RELAY_ALLOWED_HOSTS is required in Production.");
        var kickEnabled = config.GetValue<bool>("KICK_SUBSCRIPTIONS_ENABLED");
        if (kickEnabled)
        {
            if (!PublicHttpsUrl(config["RELAY_PUBLIC_BASE_URL"], out var publicUrl)) errors.Add("Public Relay HTTPS base URL required for provider subscription.");
            else if (config["KICK_CONFIGURED_WEBHOOK_URL"] != new Uri(publicUrl!, "webhooks/kick").AbsoluteUri)
                errors.Add("KICK_CONFIGURED_WEBHOOK_URL must confirm the Relay callback configured in the Kick developer application.");
            if (!development && publicUrl is not null && !(config["RELAY_ALLOWED_HOSTS"] ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(publicUrl.Host, StringComparer.OrdinalIgnoreCase))
                errors.Add("RELAY_ALLOWED_HOSTS must include the public Relay hostname.");
            if (Placeholder(config["KICK_APP_ID"])) errors.Add("KICK_APP_ID is required when Kick subscriptions are enabled.");
            try { using var rsa = RSA.Create(); rsa.ImportFromPem(RelaySecurity.KickPublicKey(config) ?? ""); }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException) { errors.Add("A valid Kick verification public key is required."); }
        }
        if (config.GetValue<bool>("FACEBOOK_WEBHOOKS_ENABLED") &&
            (Placeholder(config["FACEBOOK_APP_SECRET"]) || Placeholder(config["FACEBOOK_WEBHOOK_VERIFY_TOKEN"])))
            errors.Add("Facebook app secret and verification token are required when Facebook webhooks are enabled.");
        if (checkStorage)
        {
            try
            {
                var path = Path.GetFullPath(config["RELAY_STORAGE_PATH"] ?? Path.Combine(AppContext.BaseDirectory, "relay-state.json"));
                if (Directory.Exists(path)) throw new IOException();
                var directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".relay-preflight-" + Guid.NewGuid().ToString("N"));
                try { using (File.Open(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { } }
                finally { if (File.Exists(probe)) File.Delete(probe); }
                if (File.Exists(path)) { using var writable = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { errors.Add("Relay persistence path must be a readable file in a writable directory."); }
        }
        return errors;
    }
    private static bool Placeholder(string? value) => string.IsNullOrWhiteSpace(value) || value.Contains("REPLACE", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("changeme", StringComparison.OrdinalIgnoreCase) || value.Contains("development-secret", StringComparison.OrdinalIgnoreCase);
}
