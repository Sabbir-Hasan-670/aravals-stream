namespace AravalsStream.Core.Settings;

public enum RelaySubscriptionState { NotConfigured, Pending, Active, Error, Checking, ReauthorizationRequired }

public sealed record KickRelaySubscriptionStatus(string ChannelId, RelaySubscriptionState State,
    DateTimeOffset? LastCheckedUtc, string? Error, string CallbackUrl, KickRelaySubscription[] Subscriptions,
    string[] UncertainEvents);
public sealed record KickRelaySubscription(string Id, string Event, int Version, string AppId);

public sealed class RelaySettings
{
    public bool Enabled { get; set; }
    public string RelayUrl { get; set; } = "";
    public string InstallationId { get; set; } = "";
    public string RefreshTokenReference { get; set; } = "";
    public string[] Channels { get; set; } = [];
    public RelaySubscriptionState KickSubscriptionState { get; set; } = RelaySubscriptionState.NotConfigured;
    public bool ManageKickSubscriptions { get; set; }
    public DateTimeOffset? KickSubscriptionLastCheckedUtc { get; set; }
    public string? KickSubscriptionError { get; set; }
}
