namespace AravalsStream.Core.Settings;

public sealed class TwitchOAuthSettings
{
    public string ClientId { get; set; } = string.Empty;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}
