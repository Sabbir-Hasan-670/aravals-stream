namespace AravalsStream.Core.Chat;

public sealed record PlatformEventCapabilities(bool SupportsPaidMessage = false,
    bool SupportsSubscription = false, bool SupportsGiftSubscription = false,
    bool SupportsCheer = false, bool SupportsRaid = false, bool SupportsFollow = false,
    bool SupportsMembership = false, bool SupportsRealtimeWebhook = false)
{
    public static PlatformEventCapabilities For(string platform) => platform.ToLowerInvariant() switch
    {
        "youtube" => new(SupportsPaidMessage: true, SupportsMembership: true),
        "twitch" => new(SupportsSubscription: true, SupportsGiftSubscription: true,
            SupportsCheer: true, SupportsRaid: true,
            SupportsRealtimeWebhook: true),
        "kick" => new(SupportsSubscription: true, SupportsGiftSubscription: true,
            SupportsFollow: true, SupportsRealtimeWebhook: true),
        _ => new()
    };
}
