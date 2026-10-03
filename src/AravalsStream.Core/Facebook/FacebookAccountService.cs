using AravalsStream.Core.Accounts;
using AravalsStream.Core.Interfaces;

namespace AravalsStream.Core.Facebook;

public sealed class FacebookAccountService
{
    private readonly FacebookGraphClient _graph;
    private readonly ISecretStorage _secrets;
    public FacebookAccountService(FacebookGraphClient graph, ISecretStorage secrets)
    { _graph = graph; _secrets = secrets; }

    // Advanced developer workflow: token must be issued by an authorized Meta app.
    // This is not a substitute for Facebook Login for arbitrary users.
    public async Task<FacebookAccount> ImportUserTokenAsync(string userToken,
        FacebookAccount? previous = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userToken)) throw new ArgumentException("Enter a Meta user access token.");
        var user = await _graph.GetUserAsync(userToken, ct);
        var pages = await _graph.GetPagesAsync(userToken, ct);
        IReadOnlySet<string> permissions;
        try { permissions = await _graph.GetGrantedPermissionsAsync(userToken, ct); }
        catch (HttpRequestException) { permissions = new HashSet<string>(); }
        if (user.Id.Length == 0) throw new InvalidOperationException("Meta did not return a user identity.");
        var account = new FacebookAccount
        {
            Id = previous?.Id ?? Guid.NewGuid(), FacebookUserId = user.Id, DisplayName = user.Name,
            AvatarUrl = user.PictureUrl, TokenReference = Guid.NewGuid().ToString(),
            Connected = true, State = PlatformAccountState.Connected,
            SelectedPageId = pages.Any(p => p.Id == previous?.SelectedPageId) ? previous!.SelectedPageId : pages.FirstOrDefault()?.Id,
            GrantedPermissions = [.. permissions],
            Pages = pages.Select(p => new FacebookPageIdentity
            {
                PageId = p.Id, PageName = p.Name, PagePictureUrl = p.PictureUrl,
                PageTokenReference = Guid.NewGuid().ToString(), Tasks = [.. p.Tasks]
            }).ToList()
        };
        var stored = new List<string>();
        try
        {
            await _secrets.StoreAsync(account.TokenReference, userToken, ct);
            stored.Add(account.TokenReference);
            foreach (var page in account.Pages)
            {
                var token = pages.First(p => p.Id == page.PageId).AccessToken;
                await _secrets.StoreAsync(page.PageTokenReference!, token, ct);
                stored.Add(page.PageTokenReference!);
            }
        }
        catch
        {
            foreach (var reference in stored) await _secrets.RemoveAsync(reference, CancellationToken.None);
            throw;
        }
        if (previous != null) await RemoveTokensAsync(previous, ct);
        return account;
    }

    public async Task<string> GetPageTokenAsync(FacebookAccount account, string pageId,
        CancellationToken ct = default)
    {
        if (!account.Connected || account.State == PlatformAccountState.NeedsReauthentication)
            throw new InvalidOperationException("Reconnect the Facebook Page token.");
        var page = account.Pages.FirstOrDefault(p => p.PageId == pageId);
        if (string.IsNullOrEmpty(page?.PageTokenReference))
            throw new InvalidOperationException("The selected Facebook Page is no longer authorized.");
        var token = await _secrets.GetAsync(page.PageTokenReference, ct);
        if (string.IsNullOrEmpty(token))
        {
            account.State = PlatformAccountState.NeedsReauthentication;
            throw new InvalidOperationException("Reconnect the Facebook Page token.");
        }
        return token;
    }

    public async Task DisconnectAsync(FacebookAccount account, CancellationToken ct = default)
    {
        await RemoveTokensAsync(account, ct);
        account.TokenReference = null;
        account.SelectedPageId = null;
        account.Pages.Clear();
        account.Connected = false;
        account.State = PlatformAccountState.Disconnected;
    }

    private async Task RemoveTokensAsync(FacebookAccount account, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(account.TokenReference)) await _secrets.RemoveAsync(account.TokenReference, ct);
        foreach (var page in account.Pages)
            if (!string.IsNullOrEmpty(page.PageTokenReference)) await _secrets.RemoveAsync(page.PageTokenReference, ct);
    }
}
