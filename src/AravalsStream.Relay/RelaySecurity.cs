using System.Security.Cryptography;
using System.Text;

namespace AravalsStream.Relay;

public static class RelaySecurity
{
    public static string? KickPublicKey(IConfiguration config)
    {
        var path = config["KICK_WEBHOOK_PUBLIC_KEY_FILE"];
        if (string.IsNullOrWhiteSpace(path)) return config["KICK_WEBHOOK_PUBLIC_KEY_PEM"]?.Replace("\\n", "\n", StringComparison.Ordinal);
        try { return new FileInfo(path).Length <= 16384 ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    public static bool FixedTimeSecretEquals(string? supplied, string? expected)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(expected)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    public static bool VerifyFacebookSignature(ReadOnlySpan<byte> body, string? signatureHeader, string? appSecret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrEmpty(appSecret) ||
            !signatureHeader.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        byte[] provided;
        try { provided = Convert.FromHexString(signatureHeader[7..]); }
        catch (FormatException) { return false; }
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var expected = hmac.ComputeHash(body.ToArray());
        return provided.Length == expected.Length && CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    public static bool VerifyKickSignature(ReadOnlySpan<byte> body, string? messageId, string? timestamp,
        string? signatureBase64, string? publicKeyPem, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 200 ||
            string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signatureBase64) ||
            string.IsNullOrWhiteSpace(publicKeyPem) ||
            !DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var issued) ||
            (now - issued).Duration() > TimeSpan.FromMinutes(5)) return false;
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64); }
        catch (FormatException) { return false; }
        var prefix = Encoding.UTF8.GetBytes($"{messageId}.{timestamp}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0); body.CopyTo(signed.AsSpan(prefix.Length));
        try
        {
            using var rsa = RSA.Create(); rsa.ImportFromPem(publicKeyPem);
            return rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { return false; }
    }
}
