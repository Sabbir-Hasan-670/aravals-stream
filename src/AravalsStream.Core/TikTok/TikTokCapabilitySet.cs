namespace AravalsStream.Core.TikTok;

public enum TikTokCapabilityStatus
{
    Supported,
    RequiresPermission,
    RequiresAppReview,
    UnsupportedByCurrentPlatformApi
}

public enum TikTokAppApprovalStatus
{
    Development,
    PendingReview,
    Approved,
    AuditRequired,
    Limited
}

public sealed class TikTokCapabilitySet
{
    public const string ApiVersion = "v2";
    public const string DefaultAuthorizeBaseUrl = "https://www.tiktok.com/v2/auth/authorize/";
    public const string DefaultTokenUrl = "https://open.tiktokapis.com/v2/oauth/token/";
    public const string DefaultUserInfoUrl = "https://open.tiktokapis.com/v2/user/info/";
    public const string DefaultRedirectUri = "http://127.0.0.1:19455/callback/";

    public TikTokCapabilityStatus NativeDesktopLogin => TikTokCapabilityStatus.Supported;
    public TikTokCapabilityStatus ProfileRead => TikTokCapabilityStatus.Supported;
    public TikTokCapabilityStatus ContentPosting => TikTokCapabilityStatus.RequiresAppReview;
    public TikTokCapabilityStatus LiveCreation => TikTokCapabilityStatus.UnsupportedByCurrentPlatformApi;
    public TikTokCapabilityStatus LiveIngest => TikTokCapabilityStatus.UnsupportedByCurrentPlatformApi;
    public TikTokCapabilityStatus LiveChatRead => TikTokCapabilityStatus.UnsupportedByCurrentPlatformApi;
    public TikTokCapabilityStatus LiveChatSend => TikTokCapabilityStatus.UnsupportedByCurrentPlatformApi;
    public TikTokCapabilityStatus LiveViewerStats => TikTokCapabilityStatus.UnsupportedByCurrentPlatformApi;
    public TikTokCapabilityStatus LiveEvents => TikTokCapabilityStatus.UnsupportedByCurrentPlatformApi;

    public bool SupportsNativeLogin => NativeDesktopLogin == TikTokCapabilityStatus.Supported;
    public bool SupportsProfileRead => ProfileRead == TikTokCapabilityStatus.Supported;
    public bool SupportsVideoList => false;
    public bool SupportsContentPosting => ContentPosting == TikTokCapabilityStatus.Supported;
    public bool SupportsLiveCreation => LiveCreation == TikTokCapabilityStatus.Supported;
    public bool SupportsLiveIngest => LiveIngest == TikTokCapabilityStatus.Supported;
    public bool SupportsLiveChatRead => LiveChatRead == TikTokCapabilityStatus.Supported;
    public bool SupportsLiveChatSend => LiveChatSend == TikTokCapabilityStatus.Supported;
    public bool SupportsLiveViewerStats => LiveViewerStats == TikTokCapabilityStatus.Supported;
    public bool SupportsLiveEvents => LiveEvents == TikTokCapabilityStatus.Supported;

    public bool RequiresServerSecret => true;
    public bool RequiresAppReview => true;
    public bool RequiresAudit => true;
    public bool RequiresPkce => true;

    public TikTokAppApprovalStatus AppApprovalStatus { get; init; } = TikTokAppApprovalStatus.Development;

    public static readonly TikTokCapabilitySet Current = new();
}
