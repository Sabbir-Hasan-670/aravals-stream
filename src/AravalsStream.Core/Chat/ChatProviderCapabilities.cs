namespace AravalsStream.Core.Chat;

public sealed record ChatProviderCapabilities(bool CanReceive, bool CanSend,
    bool SupportsBadges = false, bool SupportsReplies = false,
    bool SupportsModeration = false, bool SupportsPaidMessages = false,
    bool RequiresWebhook = false, string? UnavailableReason = null)
{
    public static ChatProviderCapabilities For(string platform) => platform.ToLowerInvariant() switch
    {
        "youtube" => new(true, true, true, false, false, true),
        "twitch" => new(true, true, true, false, false, true),
        "facebook" => new(true, true, false, true, false, false),
        "kick" => new(false, true, true, false, false, false, true,
            "Receiving chat requires a public Kick webhook service."),
        "tiktok" => new(false, false, UnavailableReason:
            "LIVE chat is unavailable through the current official TikTok developer API."),
        _ => new(false, false, UnavailableReason: "Chat is unavailable for this platform.")
    };
}
