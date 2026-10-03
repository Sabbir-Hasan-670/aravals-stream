using AravalsStream.Core.Models;

namespace AravalsStream.Core.Alerts;

public enum AlertPosition { TopLeft, TopCenter, TopRight, Center, BottomLeft, BottomCenter, BottomRight, Custom }
public enum AlertAnimation { None, Fade, SlideDown, SlideUp, Zoom, Pop }

public sealed class AlertDefinition
{
    public StreamEventType EventType { get; set; }
    public bool Enabled { get; set; } = true;
    public string TitleTemplate { get; set; } = "{user}";
    public string MessageTemplate { get; set; } = "{platform} alert";
    public string FontFamily { get; set; } = "Segoe UI";
    public int FontSize { get; set; } = 38;
    public string TextAlignment { get; set; } = "Center";
    public double BackgroundOpacity { get; set; } = 0.86;
    public AlertAnimation Animation { get; set; } = AlertAnimation.Fade;
    public int EnterMilliseconds { get; set; } = 300;
    public int DisplayMilliseconds { get; set; } = 5000;
    public int ExitMilliseconds { get; set; } = 300;
    public string? SoundPath { get; set; }
    public double SoundVolume { get; set; } = 0.75;
    public bool MonitorSound { get; set; }
    public bool RecordSound { get; set; } = true;
    public List<string> SoundPlatforms { get; set; } = ["YouTube", "Twitch", "Kick", "Facebook", "TikTok", "Custom"];
    public string? ImagePath { get; set; }
    public AlertPosition Position { get; set; } = AlertPosition.TopCenter;
    public double Padding { get; set; } = 20;
    public double CornerRadius { get; set; } = 18;
    public bool ShowHorizontal { get; set; } = true;
    public bool ShowVertical { get; set; } = true;
    public int HorizontalX { get; set; } = 610;
    public int HorizontalY { get; set; } = 80;
    public int VerticalX { get; set; } = 45;
    public int VerticalY { get; set; } = 250;
    public int HorizontalWidth { get; set; } = 700;
    public int VerticalWidth { get; set; } = 900;
    public AlertDefinition Copy() => (AlertDefinition)MemberwiseClone();
}

public sealed class ChatOverlaySettings
{
    public bool Enabled { get; set; }
    public List<string> Platforms { get; set; } = ["YouTube", "Twitch", "Facebook"];
    public int MaxMessages { get; set; } = 5;
    public int MessageDurationSeconds { get; set; } = 15;
    public int FontSize { get; set; } = 26;
    public bool ShowAvatars { get; set; }
    public bool ShowBadges { get; set; } = true;
    public bool ShowTimestamps { get; set; }
    public bool ShowPlatformIcon { get; set; } = true;
    public double BackgroundOpacity { get; set; } = 0.65;
    public AlertAnimation Animation { get; set; } = AlertAnimation.Fade;
    public bool HideLinks { get; set; }
    public List<string> BlockedWords { get; set; } = [];
    public List<string> BlockedUserIds { get; set; } = [];
}

public sealed class AlertSettings
{
    public int MaxQueueLength { get; set; } = 20;
    public int GapMilliseconds { get; set; } = 500;
    public int CooldownMilliseconds { get; set; } = 1000;
    public bool GroupRepeatedEvents { get; set; } = true;
    public int GroupWindowMilliseconds { get; set; } = 5000;
    public bool SaveChatLog { get; set; }
    public List<AlertDefinition> Definitions { get; set; } = Defaults();
    public ChatOverlaySettings ChatOverlay { get; set; } = new();

    public static List<AlertDefinition> Defaults() =>
    [
        new() { EventType = StreamEventType.Follow, TitleTemplate = "New follower", MessageTemplate = "{user} followed!" },
        new() { EventType = StreamEventType.Subscription, TitleTemplate = "New subscriber", MessageTemplate = "{user} subscribed!" },
        new() { EventType = StreamEventType.GiftSubscription, TitleTemplate = "Gift subscription", MessageTemplate = "{user} gifted {count} subscriptions!" },
        new() { EventType = StreamEventType.Membership, TitleTemplate = "New member", MessageTemplate = "{user} joined!" },
        new() { EventType = StreamEventType.PaidMessage, TitleTemplate = "Paid message", MessageTemplate = "{user} sent {amount} {currency}" },
        new() { EventType = StreamEventType.Cheer, TitleTemplate = "Cheer", MessageTemplate = "{user} cheered {count}!" },
        new() { EventType = StreamEventType.Raid, TitleTemplate = "Raid", MessageTemplate = "{user} raided with {count} viewers!" },
        new() { EventType = StreamEventType.Custom, TitleTemplate = "Test alert", MessageTemplate = "Hello from Aravals Stream!" }
    ];
}
