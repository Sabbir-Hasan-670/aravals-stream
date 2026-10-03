namespace AravalsStream.Core.Settings;

public sealed class TikTokOAuthSettings
{
    public string ClientKey { get; set; } = "";
    public string? ClientSecretReference { get; set; }
    public string RedirectUri { get; set; } = "http://127.0.0.1:19455/callback/";
    public string Scopes { get; set; } = "user.info.basic";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientKey) && !string.IsNullOrWhiteSpace(ClientSecretReference);
}
