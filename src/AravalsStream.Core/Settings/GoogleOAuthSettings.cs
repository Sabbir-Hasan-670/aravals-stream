namespace AravalsStream.Core.Settings;

public sealed class GoogleOAuthSettings
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Scopes { get; set; } = "https://www.googleapis.com/auth/youtube.force-ssl https://www.googleapis.com/auth/youtube.readonly";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}
