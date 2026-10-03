using System.Net.Http;
using System.Windows;
using AravalsStream.Core.Facebook;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Services;

namespace AravalsStream.App.Views;

public partial class MainWindow
{
    private FacebookChatProvider? _facebookChatProvider;

    private async Task ConfigureNativeFacebookDestinationAsync(PlatformProfile profile)
    {
        var account = _loadedSettings.FacebookAccount;
        if (account?.Connected != true || account.Pages.Count == 0)
        {
            MessageBox.Show(this, "Configure an authorized Facebook Page under Settings → Accounts, or choose Manual RTMP.",
                "Facebook Page required");
            Settings_Click(this, new RoutedEventArgs());
            return;
        }
        var dialog = new FacebookLiveDialog(this, account);
        AravalsStream.App.Controls.DarkWindowChrome.Apply(dialog);
        if (dialog.ShowDialog() != true) return;
        var group = PlatformDestinationGroup.CreateFromProfile(profile);
        group.Name = $"Facebook ({dialog.PageName})";
        group.ConfigurationMode = ConfigurationMode.NativeApi;
        group.BoundAccountId = account.Id.ToString();
        group.FacebookPageId = dialog.PageId;
        group.BroadcastTitle = dialog.StreamTitle;
        group.FacebookDescription = dialog.Description;
        group.ServerUrl = "";
        group.Horizontal.StreamUrl = "";
        group.Vertical.StreamUrl = "";
        group.Routing = RoutingMode.Horizontal;
        group.Order = ViewModel.DestinationGroups.Count;
        group.EnsureChildDestinations();
        ViewModel.DestinationGroups.Add(group);
        DestinationItems.Items.Refresh();
        await SaveSettingsAsync();
        EditDestinationGroup(group, skipNativeMetadata: true);
    }

    private async Task EditNativeFacebookMetadataAsync(PlatformDestinationGroup group)
    {
        var account = _loadedSettings.FacebookAccount;
        if (account?.Connected != true || group.BoundAccountId != account.Id.ToString()) return;
        var dialog = new FacebookLiveDialog(this, account, group.FacebookPageId,
            group.BroadcastTitle, group.FacebookDescription);
        AravalsStream.App.Controls.DarkWindowChrome.Apply(dialog);
        if (dialog.ShowDialog() != true) return;
        group.FacebookPageId = dialog.PageId;
        group.BroadcastTitle = dialog.StreamTitle;
        group.FacebookDescription = dialog.Description;
        await SaveSettingsAsync();
    }

    private async Task PrepareNativeFacebookOutputAsync(PlatformDestinationGroup group)
    {
        var account = _loadedSettings.FacebookAccount;
        if (account?.Connected != true || group.BoundAccountId != account.Id.ToString() ||
            string.IsNullOrEmpty(group.FacebookPageId))
            throw new InvalidOperationException("Reconnect the Facebook Page bound to this destination.");
        if (group.Routing == RoutingMode.Both)
            throw new InvalidOperationException("Native Facebook Page uses one output. Choose Horizontal or Vertical.");
        var token = await new FacebookAccountService(new FacebookGraphClient(), _secrets)
            .GetPageTokenAsync(account, group.FacebookPageId);
        FacebookLive live;
        try
        {
            live = await new FacebookGraphClient().CreatePageLiveAsync(group.FacebookPageId, token,
                group.BroadcastTitle ?? "Aravals Stream", group.FacebookDescription);
        }
        catch (FacebookGraphException ex)
        {
            account.State = ex.GraphCode == 190
                ? AravalsStream.Core.Accounts.PlatformAccountState.NeedsReauthentication
                : AravalsStream.Core.Accounts.PlatformAccountState.PermissionMissing;
            throw;
        }
        var (server, key) = FacebookIngest.Split(live.SecureStreamUrl);
        group.StreamKeyReference ??= Guid.NewGuid().ToString();
        _secrets.Set(group.StreamKeyReference, key);
        group.ServerUrl = server;
        foreach (var child in new[] { group.Horizontal, group.Vertical })
        {
            child.StreamUrl = server;
            child.StreamKeyReference = group.StreamKeyReference;
            child.Protocol = "RTMPS";
        }
        group.BroadcastId = live.Id;
        group.BroadcastStatus = "Live requested; platform status unverified";
        ViewModel.FacebookBroadcastStatus = group.BroadcastStatus;
        ViewModel.FacebookPageName = account.Pages.FirstOrDefault(p => p.PageId == group.FacebookPageId)?.PageName ?? "—";
        await SaveSettingsAsync();
    }

    private void StartFacebookServicesIfNeeded()
    {
        if (_facebookChatProvider != null) return;
        var group = ViewModel.DestinationGroups.FirstOrDefault(g => g.PlatformType == PlatformType.Facebook &&
            g.ConfigurationMode == ConfigurationMode.NativeApi && !string.IsNullOrEmpty(g.BroadcastId) &&
            (_outputs.ContainsKey(g.Horizontal.Id) || _outputs.ContainsKey(g.Vertical.Id)));
        var account = _loadedSettings.FacebookAccount;
        if (group == null || account?.Connected != true || group.FacebookPageId == null) return;
        ViewModel.FacebookBroadcastStatus = group.BroadcastStatus ?? "Live requested; platform status unverified";
        ViewModel.FacebookPageName = account.Pages.FirstOrDefault(p => p.PageId == group.FacebookPageId)?.PageName ?? "—";
        var pageId = group.FacebookPageId;
        var service = new FacebookAccountService(new FacebookGraphClient(), _secrets);
        var chat = new FacebookChatProvider(new FacebookGraphClient(), ct => service.GetPageTokenAsync(account, pageId, ct));
        chat.StatusChanged += status => Dispatcher.BeginInvoke(() => ViewModel.FacebookCommentsStatus = status);
        _unifiedChat.Register(chat);
        _facebookChatProvider = chat;
        _ = chat.StartAsync(group.BroadcastId!);
    }

    private void CheckIfFacebookStillActive()
    {
        if (!ViewModel.DestinationGroups.Any(g => g.PlatformType == PlatformType.Facebook &&
            g.ConfigurationMode == ConfigurationMode.NativeApi &&
            (_outputs.ContainsKey(g.Horizontal.Id) || _outputs.ContainsKey(g.Vertical.Id))))
            _ = StopFacebookServicesAsync();
    }

    private async Task StopFacebookServicesAsync()
    {
        _unifiedChat.Unregister("Facebook");
        var chat = _facebookChatProvider; _facebookChatProvider = null;
        if (chat != null) await chat.StopAsync();
        ViewModel.FacebookBroadcastStatus = "OFFLINE";
        ViewModel.FacebookCommentsStatus = "Offline";
        ViewModel.FacebookViewers = "—";
    }
}
