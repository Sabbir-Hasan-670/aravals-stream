using AravalsStream.Core.Accounts;

namespace AravalsStream.Core.Facebook;

public enum FacebookPermissionStatus { Granted, Missing, RequiresReview, Unavailable }

public sealed record FacebookPermissionDiagnostic(string Label, FacebookPermissionStatus Status);

public static class FacebookPermissionDiagnostics
{
    public static IReadOnlyList<FacebookPermissionDiagnostic> For(FacebookAccount? account)
    {
        if (account?.Connected != true)
            return [new("Page access", FacebookPermissionStatus.Unavailable),
                new("Live creation", FacebookPermissionStatus.Unavailable),
                new("Comment read", FacebookPermissionStatus.Unavailable),
                new("Comment reply", FacebookPermissionStatus.Unavailable)];
        var granted = account.GrantedPermissions.ToHashSet(StringComparer.Ordinal);
        FacebookPermissionStatus Check(params string[] required) => required.All(granted.Contains)
            ? FacebookPermissionStatus.Granted : FacebookPermissionStatus.Missing;
        return [
            new("Page access", account.Pages.Count > 0 ? FacebookPermissionStatus.Granted : Check("pages_show_list")),
            new("Live creation", Check("pages_manage_posts", "pages_read_engagement") == FacebookPermissionStatus.Granted
                ? FacebookPermissionStatus.RequiresReview : FacebookPermissionStatus.Missing),
            new("Comment read", Check("pages_read_user_content", "pages_read_engagement")),
            new("Comment reply", Check("pages_manage_engagement"))
        ];
    }
}
