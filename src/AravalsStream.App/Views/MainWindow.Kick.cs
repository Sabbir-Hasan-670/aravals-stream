using System.Windows;
using AravalsStream.Core.Kick;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Services;

namespace AravalsStream.App.Views;

public partial class MainWindow
{
    private async Task ConfigureNativeKickDestinationAsync(PlatformProfile profile)
    {
        var account = _loadedSettings.KickAccount;
        if (account?.Connected != true || string.IsNullOrEmpty(account.TokenReference))
        {
            MessageBox.Show(this, "Connect Kick under Settings → Accounts, then add the destination again.", "Kick account required");
            Settings_Click(this, new RoutedEventArgs());
            return;
        }
        try
        {
            var oauth = new KickOAuthClient(_loadedSettings.KickOAuth, _secrets);
            var token = await oauth.GetValidAccessTokenAsync(account);
            var api = new KickApiClient();
            var channel = await api.GetChannelAsync(token, account.KickUserId);
            var dialog = new KickChannelDialog(this, api, token, channel);
            AravalsStream.App.Controls.DarkWindowChrome.Apply(dialog);
            if (dialog.ShowDialog() != true) return;
            var group = PlatformDestinationGroup.CreateFromProfile(profile);
            group.Name = $"Kick ({account.DisplayName})";
            group.ConfigurationMode = ConfigurationMode.NativeApi;
            group.BoundAccountId = account.Id.ToString();
            group.BroadcastTitle = dialog.StreamTitle;
            group.KickCategoryId = dialog.CategoryId;
            group.KickCategoryName = dialog.CategoryName;
            group.KickTags = dialog.Tags;
            group.Routing = RoutingMode.Horizontal;
            group.Order = ViewModel.DestinationGroups.Count;
            if (SafeKickServerUrl(channel.StreamUrl, channel.StreamKey) is { } url)
            {
                group.ServerUrl = url;
                group.Horizontal.StreamUrl = url;
                group.Vertical.StreamUrl = url;
                group.Horizontal.Protocol = new Uri(url).Scheme.ToUpperInvariant();
                group.Vertical.Protocol = group.Horizontal.Protocol;
            }
            else
            {
                group.ServerUrl = "";
                group.Horizontal.StreamUrl = "";
                group.Vertical.StreamUrl = "";
            }
            if (!string.IsNullOrEmpty(channel.StreamKey))
            {
                group.StreamKeyReference = Guid.NewGuid().ToString();
                _secrets.Set(group.StreamKeyReference, channel.StreamKey);
                group.Horizontal.StreamKeyReference = group.StreamKeyReference;
                group.Vertical.StreamKeyReference = group.StreamKeyReference;
            }
            group.EnsureChildDestinations();
            ViewModel.DestinationGroups.Add(group);
            DestinationItems.Items.Refresh();
            await SaveSettingsAsync();
            EditDestinationGroup(group, skipNativeMetadata: true);
            if (string.IsNullOrEmpty(group.StreamKeyReference) || string.IsNullOrEmpty(group.ServerUrl))
                MessageBox.Show(this, "Kick did not return a usable stream URL and key. Enter both in the destination settings using the secure Manual RTMP fields.", "Kick ingest");
        }
        catch (Exception ex) { MessageBox.Show(this, $"Kick setup failed: {ex.Message}", "Kick"); }
    }

    private static string? SafeKickServerUrl(string? url, string? key)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "rtmp" && uri.Scheme != "rtmps") ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            (!string.IsNullOrEmpty(key) && url.Contains(key, StringComparison.Ordinal))) return null;
        return url.TrimEnd('/');
    }

    private async Task EditNativeKickMetadataAsync(PlatformDestinationGroup group)
    {
        try
        {
            var account = _loadedSettings.KickAccount;
            if (account?.Connected != true || group.BoundAccountId != account.Id.ToString()) return;
            var token = await new KickOAuthClient(_loadedSettings.KickOAuth, _secrets).GetValidAccessTokenAsync(account);
            var api = new KickApiClient();
            var channel = await api.GetChannelAsync(token, account.KickUserId);
            var dialog = new KickChannelDialog(this, api, token, channel);
            AravalsStream.App.Controls.DarkWindowChrome.Apply(dialog);
            if (dialog.ShowDialog() != true) return;
            await api.UpdateChannelAsync(token, dialog.StreamTitle, dialog.CategoryId, dialog.Tags);
            group.BroadcastTitle = dialog.StreamTitle;
            group.KickCategoryId = dialog.CategoryId;
            group.KickCategoryName = dialog.CategoryName;
            group.KickTags = dialog.Tags;
            ViewModel.KickCategory = group.KickCategoryName ?? "—";
            await SaveSettingsAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, $"Kick metadata update failed: {ex.Message}", "Kick"); }
    }

    private async Task PrepareNativeKickOutputAsync(PlatformDestinationGroup group)
    {
        var account = _loadedSettings.KickAccount;
        if (account?.Connected != true || group.BoundAccountId != account.Id.ToString())
            throw new InvalidOperationException("Reconnect the Kick account bound to this destination.");
        if (group.Routing == RoutingMode.Both)
            throw new InvalidOperationException("Native Kick supports one active output. Choose Horizontal or Vertical.");
        var token = await new KickOAuthClient(_loadedSettings.KickOAuth, _secrets).GetValidAccessTokenAsync(account);
        var api = new KickApiClient();
        var channel = await api.GetChannelAsync(token, account.KickUserId);
        if (SafeKickServerUrl(channel.StreamUrl, channel.StreamKey) is { } url)
        {
            group.ServerUrl = url;
            foreach (var child in new[] { group.Horizontal, group.Vertical })
            { child.StreamUrl = url; child.Protocol = new Uri(url).Scheme.ToUpperInvariant(); }
        }
        if (!string.IsNullOrEmpty(channel.StreamKey))
        {
            group.StreamKeyReference ??= Guid.NewGuid().ToString();
            _secrets.Set(group.StreamKeyReference, channel.StreamKey);
            group.Horizontal.StreamKeyReference = group.StreamKeyReference;
            group.Vertical.StreamKeyReference = group.StreamKeyReference;
        }
        if (string.IsNullOrWhiteSpace(group.ServerUrl) || string.IsNullOrEmpty(group.StreamKeyReference) ||
            _secrets.Get(group.StreamKeyReference) == null)
            throw new InvalidOperationException("Kick stream URL/key unavailable. Configure the destination securely before starting.");
        await api.UpdateChannelAsync(token, group.BroadcastTitle, group.KickCategoryId, group.KickTags);
        await SaveSettingsAsync();
    }

    private void StartKickServicesIfNeeded()
    {
        if (_kickChatProvider != null) return;
        var group = ViewModel.DestinationGroups.FirstOrDefault(g => g.PlatformType == PlatformType.Kick &&
            g.ConfigurationMode == ConfigurationMode.NativeApi &&
            (_outputs.ContainsKey(g.Horizontal.Id) || _outputs.ContainsKey(g.Vertical.Id)));
        var account = _loadedSettings.KickAccount;
        if (group == null || account?.Connected != true) return;
        ViewModel.KickBroadcastStatus = "LIVE";
        ViewModel.KickCategory = group.KickCategoryName ?? "—";
        var chat = new KickChatProvider(account, new KickOAuthClient(_loadedSettings.KickOAuth, _secrets), new KickApiClient());
        chat.StatusChanged += status => Dispatcher.BeginInvoke(() => ViewModel.KickChatStatus = status);
        _unifiedChat.Register(chat);
        _kickChatProvider = chat;
        _ = StartKickChatSafeAsync(chat, account.KickUserId);
        _kickStatsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _kickStatsTimer.Tick += async (_, _) => await PollKickStatsAsync(account.KickUserId);
        _kickStatsTimer.Start();
        _ = PollKickStatsAsync(account.KickUserId);
    }

    private async Task StartKickChatSafeAsync(KickChatProvider chat, string userId)
    {
        try { await chat.StartAsync(userId); }
        catch (Exception ex)
        {
            AppLog.Write("KickChat", $"Chat send unavailable independently: {ex.Message}");
            ViewModel.KickChatStatus = "Error";
        }
    }

    private async Task PollKickStatsAsync(string userId)
    {
        try
        {
            var account = _loadedSettings.KickAccount;
            if (account == null) return;
            var token = await new KickOAuthClient(_loadedSettings.KickOAuth, _secrets).GetValidAccessTokenAsync(account);
            var channel = await new KickApiClient().GetChannelAsync(token, userId);
            ViewModel.KickViewers = channel.IsLive ? channel.Viewers?.ToString("N0") ?? "—" : "—";
            ViewModel.KickFollowers = "—";
            ViewModel.KickCategory = channel.CategoryName is { Length: > 0 } name ? name : "—";
        }
        catch (Exception ex) { AppLog.Write("KickStats", $"Platform statistics unavailable: {ex.Message}"); }
    }

    private void CheckIfKickStillActive()
    {
        if (!ViewModel.DestinationGroups.Any(g => g.PlatformType == PlatformType.Kick &&
            g.ConfigurationMode == ConfigurationMode.NativeApi &&
            (_outputs.ContainsKey(g.Horizontal.Id) || _outputs.ContainsKey(g.Vertical.Id))))
            _ = StopKickServicesAsync();
    }

    private async Task StopKickServicesAsync()
    {
        _unifiedChat.Unregister("Kick");
        _kickStatsTimer?.Stop(); _kickStatsTimer = null;
        var chat = _kickChatProvider; _kickChatProvider = null;
        if (chat != null) await chat.StopAsync();
        ViewModel.KickBroadcastStatus = "OFFLINE";
        ViewModel.KickChatStatus = "Offline";
        ViewModel.KickViewers = "—";
    }
}
