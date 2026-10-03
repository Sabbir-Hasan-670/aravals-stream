using System.Windows;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Services;
using AravalsStream.Core.Twitch;

namespace AravalsStream.App.Views;

public partial class MainWindow
{
    private async Task EditNativeTwitchMetadataAsync(PlatformDestinationGroup group)
    {
        try
        {
            var account = _loadedSettings.TwitchAccount;
            if (account?.Connected != true || group.BoundAccountId != account.Id.ToString()) return;
            var token = await new TwitchOAuthClient(_loadedSettings.TwitchOAuth, _secrets).GetValidAccessTokenAsync(account);
            var api = new TwitchApiClient(_loadedSettings.TwitchOAuth.ClientId);
            var channel = await api.GetChannelAsync(token, account.UserId);
            var dialog = new TwitchChannelDialog(this, api, token, channel);
            AravalsStream.App.Controls.DarkWindowChrome.Apply(dialog);
            if (dialog.ShowDialog() != true) return;
            await api.UpdateChannelAsync(token, account.UserId, dialog.StreamTitle, dialog.GameId,
                dialog.ChannelLanguage, dialog.Tags);
            group.BroadcastTitle = dialog.StreamTitle;
            group.TwitchGameId = dialog.GameId;
            group.TwitchGameName = dialog.GameName;
            group.TwitchLanguage = dialog.ChannelLanguage;
            group.TwitchTags = dialog.Tags;
            if (dialog.Ingest != null) group.TwitchIngestName = dialog.Ingest.Name;
            ViewModel.TwitchCategory = group.TwitchGameName ?? "—";
            await SaveSettingsAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, $"Twitch metadata update failed: {ex.Message}", "Twitch"); }
    }

    private async Task ConfigureNativeTwitchDestinationAsync(PlatformProfile profile)
    {
        var account = _loadedSettings.TwitchAccount;
        if (account?.Connected != true || string.IsNullOrEmpty(account.TokenReference))
        {
            MessageBox.Show(this, "Connect Twitch under Settings → Accounts, then add the destination again.", "Twitch account required");
            Settings_Click(this, new RoutedEventArgs());
            return;
        }
        try
        {
            var oauth = new TwitchOAuthClient(_loadedSettings.TwitchOAuth, _secrets);
            var token = await oauth.GetValidAccessTokenAsync(account);
            var api = new TwitchApiClient(_loadedSettings.TwitchOAuth.ClientId);
            var channel = await api.GetChannelAsync(token, account.UserId);
            var dialog = new TwitchChannelDialog(this, api, token, channel);
            AravalsStream.App.Controls.DarkWindowChrome.Apply(dialog);
            if (dialog.ShowDialog() != true || dialog.Ingest is not { } ingest) return;
            var group = PlatformDestinationGroup.CreateFromProfile(profile);
            group.Name = $"Twitch (@{account.Login})";
            group.ConfigurationMode = ConfigurationMode.NativeApi;
            group.BoundAccountId = account.Id.ToString();
            group.BroadcastTitle = dialog.StreamTitle;
            group.TwitchGameId = dialog.GameId;
            group.TwitchGameName = dialog.GameName;
            group.TwitchLanguage = dialog.ChannelLanguage;
            group.TwitchTags = dialog.Tags;
            group.TwitchIngestName = ingest.Name;
            group.ServerUrl = ingest.ServerUrl;
            group.Horizontal.Protocol = new Uri(ingest.ServerUrl).Scheme.ToUpperInvariant();
            group.Vertical.Protocol = group.Horizontal.Protocol;
            group.Horizontal.StreamUrl = ingest.ServerUrl;
            group.Vertical.StreamUrl = ingest.ServerUrl;
            group.Routing = RoutingMode.Horizontal;
            group.Order = ViewModel.DestinationGroups.Count;
            group.EnsureChildDestinations();
            ViewModel.DestinationGroups.Add(group);
            DestinationItems.Items.Refresh();
            await SaveSettingsAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, $"Twitch setup failed: {ex.Message}", "Twitch"); }
    }

    private async Task PrepareNativeTwitchOutputAsync(PlatformDestinationGroup group)
    {
        var account = _loadedSettings.TwitchAccount;
        if (account?.Connected != true || group.BoundAccountId != account.Id.ToString())
            throw new InvalidOperationException("Reconnect the Twitch account bound to this destination.");
        if (group.Routing == RoutingMode.Both)
            throw new InvalidOperationException("Native Twitch supports one active output. Choose Horizontal or Vertical; use Manual RTMP for another destination.");
        var oauth = new TwitchOAuthClient(_loadedSettings.TwitchOAuth, _secrets);
        var token = await oauth.GetValidAccessTokenAsync(account);
        var api = new TwitchApiClient(_loadedSettings.TwitchOAuth.ClientId);
        var ingests = await api.GetIngestServersAsync();
        var ingest = ingests.FirstOrDefault(x => x.Name == group.TwitchIngestName)
            ?? ingests.FirstOrDefault(x => x.IsDefault) ?? ingests.FirstOrDefault()
            ?? throw new InvalidOperationException("Twitch ingest discovery returned no usable servers.");
        var key = await api.GetStreamKeyAsync(token, account.UserId);
        group.StreamKeyReference ??= Guid.NewGuid().ToString();
        _secrets.Set(group.StreamKeyReference, key);
        group.ServerUrl = ingest.ServerUrl;
        foreach (var child in new[] { group.Horizontal, group.Vertical })
        {
            child.StreamUrl = ingest.ServerUrl;
            child.Protocol = new Uri(ingest.ServerUrl).Scheme.ToUpperInvariant();
            child.StreamKeyReference = group.StreamKeyReference;
        }
        if (group.BroadcastTitle != null || group.TwitchGameId != null || group.TwitchLanguage != null || group.TwitchTags.Count > 0)
            await api.UpdateChannelAsync(token, account.UserId, group.BroadcastTitle, group.TwitchGameId,
                group.TwitchLanguage, group.TwitchTags);
        _outputErrors.Remove(group.Horizontal.Id);
        _outputErrors.Remove(group.Vertical.Id);
        await SaveSettingsAsync();
    }

    private void StartTwitchServicesIfNeeded()
    {
        if (_twitchChatProvider != null) return;
        var group = ViewModel.DestinationGroups.FirstOrDefault(g => g.PlatformType == PlatformType.Twitch &&
            g.ConfigurationMode == ConfigurationMode.NativeApi &&
            (_outputs.ContainsKey(g.Horizontal.Id) || _outputs.ContainsKey(g.Vertical.Id)));
        var account = _loadedSettings.TwitchAccount;
        if (group == null || account?.Connected != true) return;
        ViewModel.TwitchBroadcastStatus = "LIVE";
        ViewModel.TwitchCategory = group.TwitchGameName ?? "—";
        var oauth = new TwitchOAuthClient(_loadedSettings.TwitchOAuth, _secrets);
        var api = new TwitchApiClient(_loadedSettings.TwitchOAuth.ClientId);
        var chat = new TwitchChatProvider(account, oauth, api);
        _unifiedChat.Register(chat);
        chat.StatusChanged += status => Dispatcher.BeginInvoke(() => ViewModel.TwitchChatStatus = status);
        _twitchChatProvider = chat;
        _ = StartTwitchChatSafeAsync(chat, account.UserId);
        _twitchStatsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _twitchStatsTimer.Tick += async (_, _) => await PollTwitchStatsAsync(account.UserId);
        _twitchStatsTimer.Start();
        _ = PollTwitchStatsAsync(account.UserId);
    }

    private async Task StartTwitchChatSafeAsync(TwitchChatProvider chat, string userId)
    {
        try { await chat.StartAsync(userId); }
        catch (Exception ex)
        {
            AppLog.Write("TwitchChat", $"Chat connection failed independently: {ex.Message}");
            ViewModel.TwitchChatStatus = "Reconnecting";
        }
    }

    private async Task PollTwitchStatsAsync(string userId)
    {
        try
        {
            var account = _loadedSettings.TwitchAccount;
            if (account == null) return;
            var token = await new TwitchOAuthClient(_loadedSettings.TwitchOAuth, _secrets).GetValidAccessTokenAsync(account);
            var stats = await new TwitchApiClient(_loadedSettings.TwitchOAuth.ClientId).GetStatsAsync(token, userId);
            ViewModel.TwitchViewers = stats.Viewers?.ToString("N0") ?? "—";
            ViewModel.TwitchFollowers = stats.Followers?.ToString("N0") ?? "—";
        }
        catch (Exception ex) { AppLog.Write("TwitchStats", $"Platform statistics unavailable: {ex.Message}"); }
    }

    private void CheckIfTwitchStillActive()
    {
        if (!ViewModel.DestinationGroups.Any(g => g.PlatformType == PlatformType.Twitch &&
            g.ConfigurationMode == ConfigurationMode.NativeApi &&
            (_outputs.ContainsKey(g.Horizontal.Id) || _outputs.ContainsKey(g.Vertical.Id))))
            _ = StopTwitchServicesAsync();
    }

    private async Task StopTwitchServicesAsync()
    {
        _unifiedChat.Unregister("Twitch");
        _twitchStatsTimer?.Stop(); _twitchStatsTimer = null;
        var chat = _twitchChatProvider; _twitchChatProvider = null;
        if (chat != null) try { await chat.StopAsync(); }
            catch (Exception ex) { AppLog.Write("TwitchChat", $"Chat stop failed: {ex.Message}"); }
        ViewModel.TwitchBroadcastStatus = "OFFLINE";
        ViewModel.TwitchChatStatus = "Offline";
        ViewModel.TwitchViewers = "—";
    }
}
