namespace AravalsStream.Core.Settings;

public sealed class KickOAuthSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecretReference { get; set; } = "";
    public string RedirectUri { get; set; } = "http://localhost:8765/kick/callback/";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecretReference) && !string.IsNullOrWhiteSpace(RedirectUri);
}
