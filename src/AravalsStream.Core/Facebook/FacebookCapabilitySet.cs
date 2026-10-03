namespace AravalsStream.Core.Facebook;

public enum FacebookCapabilityStatus { Supported, RequiresPermission, RequiresAppReview, UnsupportedByCurrentMetaApi }

public sealed class FacebookCapabilitySet
{
    public const string GraphVersion = "v26.0";
    public FacebookCapabilityStatus NativeDesktopLogin => FacebookCapabilityStatus.UnsupportedByCurrentMetaApi;
    public FacebookCapabilityStatus PageSelection => FacebookCapabilityStatus.RequiresPermission;
    public FacebookCapabilityStatus PageLiveCreation => FacebookCapabilityStatus.RequiresAppReview;
    public FacebookCapabilityStatus ScheduledLive => FacebookCapabilityStatus.UnsupportedByCurrentMetaApi;
    public FacebookCapabilityStatus ApiIngest => FacebookCapabilityStatus.RequiresAppReview;
    public FacebookCapabilityStatus LiveCommentsRead => FacebookCapabilityStatus.RequiresPermission;
    public FacebookCapabilityStatus LiveCommentsSend => FacebookCapabilityStatus.RequiresPermission;
    public FacebookCapabilityStatus ViewerStats => FacebookCapabilityStatus.UnsupportedByCurrentMetaApi;
    public FacebookCapabilityStatus DesktopWebhookEvents => FacebookCapabilityStatus.UnsupportedByCurrentMetaApi;
    public bool RequiresPageAccessToken => true;
    public bool RequiresAppReview => true;
    public static readonly FacebookCapabilitySet Current = new();
}
