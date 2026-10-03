namespace AravalsStream.Core.Accounts;

public sealed class OAuthTokenData
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string? Scope { get; set; }

    public bool IsExpired(TimeSpan? buffer = null)
    {
        var buf = buffer ?? TimeSpan.FromMinutes(2);
        return DateTimeOffset.UtcNow >= (ExpiresAtUtc - buf);
    }
}
