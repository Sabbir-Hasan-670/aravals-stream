using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AravalsStream.App.Composition;
using AravalsStream.App.Audio;
using AravalsStream.Core.Alerts;
using AravalsStream.Core.Chat;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;

namespace AravalsStream.App.Views;

public partial class MainWindow
{
    private readonly UnifiedChatService _unifiedChat = new();
    private readonly ObservableCollection<string> _activity = [];
    private AlertEngine? _alertEngine;
    private AlertSoundPlayer? _alertSound;
    private AlertInstance? _lastRenderedAlert;
    private DateTimeOffset _lastAlertOverlayRender = DateTimeOffset.MinValue;
    private DateTimeOffset _lastChatOverlayFrame = DateTimeOffset.MinValue;
    private bool _chatOverlayWasVisible;
    private readonly WebhookRelayClient _relayClient = new();
    private KickRelaySubscriptionCoordinator? _kickRelaySubscriptions;

    private void InitializeAlerts()
    {
        _alertEngine = new AlertEngine(_loadedSettings.Alerts);
        _unifiedChat.ConfigureChatLog(_loadedSettings.Alerts.SaveChatLog,
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AravalsStream", "chat-logs"));
        _alertSound = new AlertSoundPlayer(_audioEngine.Mixer, () =>
            ViewModel.SelectedScene?.Sources.Any(s => s.Visible && s.Type == SourceType.AudioOutput) != true);
        _alertEngine.Activated += alert =>
        {
            if (_alertSound != null) _ = _alertSound.PlayAsync(alert.Definition, ViewModel.DestinationGroups);
        };
        ActivityList.ItemsSource = _activity;
        _unifiedChat.MessageReceived += message => Dispatcher.BeginInvoke(() =>
        {
            _lastChatOverlayFrame = DateTimeOffset.MinValue;
            AddBounded(ViewModel.AllChatMessages, message);
            var platformCollection = message.Platform.ToLowerInvariant() switch
            {
                "youtube" => ViewModel.YouTubeChatMessages,
                "twitch" => ViewModel.TwitchChatMessages,
                "kick" => ViewModel.KickChatMessages,
                "facebook" => ViewModel.FacebookChatMessages,
                _ => null
            };
            if (platformCollection != null) AddBounded(platformCollection, message);
        });
        _unifiedChat.EventReceived += item => Dispatcher.BeginInvoke(() =>
        {
            _activity.Insert(0, $"{item.Platform} • {item.EventType} • {item.ActorName}" +
                (item.Amount.HasValue ? $" • {item.Amount} {item.Currency}" : ""));
            while (_activity.Count > 100) _activity.RemoveAt(_activity.Count - 1);
            _alertEngine?.Enqueue(item);
        });
        _relayClient.StateChanged += state => AppLog.Write("Relay", $"Connection state: {state}");
        Closed += (_, _) => { if (_kickRelaySubscriptions is { } coordinator) _ = coordinator.DisposeAsync(); };
        _relayClient.EventReceived += evt =>
        {
            var data = evt.Payload;
            var actorId = StringFrom(data, "actorId"); var actorName = StringFrom(data, "actorName");
            var message = StringFrom(data, "message");
            if (evt.Provider.Equals("kick", StringComparison.OrdinalIgnoreCase))
            {
                var messageType = evt.EventType switch
                {
                    "chat.message.sent" => ChatMessageType.StandardMessage,
                    "channel.followed" => ChatMessageType.Follow,
                    "channel.subscription.new" or "channel.subscription.renewal" => ChatMessageType.Subscription,
                    "channel.subscription.gifts" => ChatMessageType.GiftSubscription,
                    _ => ChatMessageType.SystemAnnouncement
                };
                if (message.Length == 0) message = evt.EventType switch
                { "channel.followed" => "Followed the channel", "channel.subscription.gifts" => "Gifted subscriptions", _ => "Subscribed to the channel" };
                _unifiedChat.PublishVerifiedWebhook(new ChatMessage
                {
                    Id = evt.EventId, RawProviderMessageId = evt.EventId, Platform = "Kick", ChannelId = evt.ChannelId,
                    AuthorId = actorId, AuthorName = actorName, Text = message, Timestamp = evt.OccurredAt, MessageType = messageType
                });
                return;
            }
            // A generic Page feed change is not evidence of a share, reaction or donation.
            var type = StreamEventType.Custom;
            _unifiedChat.PublishRelayEvent(new StreamEvent
            {
                Id = evt.EventId, Platform = "Facebook",
                EventType = type, Timestamp = evt.OccurredAt, ActorId = actorId, ActorName = actorName,
                Message = message.Length == 0 ? evt.EventType : message,
                Metadata = new Dictionary<string, string> { ["channelId"] = evt.ChannelId, ["receivedAt"] = evt.ReceivedAt.ToString("O") }
            });
        };
        ConfigureWebhookRelay();
    }

    private static string StringFrom(System.Text.Json.JsonElement value, string name) =>
        value.ValueKind == System.Text.Json.JsonValueKind.Object && value.TryGetProperty(name, out var field) &&
        field.ValueKind == System.Text.Json.JsonValueKind.String ? field.GetString() ?? "" : "";

    private void ConfigureWebhookRelay()
    {
        if (_kickRelaySubscriptions is { } oldCoordinator) { _kickRelaySubscriptions = null; _ = oldCoordinator.DisposeAsync(); }
        _relayClient.Stop();
        var config = _loadedSettings.Relay;
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.InstallationId) || string.IsNullOrWhiteSpace(config.RefreshTokenReference)) return;
        var refresh = _secrets.Get(config.RefreshTokenReference);
        if (string.IsNullOrEmpty(refresh)) { AppLog.Write("Relay", "Saved relay credential is unavailable."); return; }
        try
        {
            if (config.ManageKickSubscriptions && _loadedSettings.KickAccount is { Connected: true } account && config.Channels.Contains("kick:" + account.KickUserId))
            {
                _kickRelaySubscriptions = new KickRelaySubscriptionCoordinator(_relayClient, account.KickUserId,
                    ct => new AravalsStream.Core.Kick.KickOAuthClient(_loadedSettings.KickOAuth, _secrets).GetValidAccessTokenAsync(account, ct));
                _kickRelaySubscriptions.StatusChanged += ApplyKickSubscriptionStatus;
            }
            else if (config.ManageKickSubscriptions)
            {
                config.KickSubscriptionState = _loadedSettings.KickAccount?.Connected == true
                    ? AravalsStream.Core.Settings.RelaySubscriptionState.Error : AravalsStream.Core.Settings.RelaySubscriptionState.ReauthorizationRequired;
                config.KickSubscriptionError = "Connect Kick and enroll the same channel with Relay before managing subscriptions.";
            }
            _relayClient.Start(config.RelayUrl, config.InstallationId, refresh, config.Channels);
        }
        catch (Exception ex) { AppLog.Write("Relay", $"Optional relay startup failed: {ex.GetType().Name}"); }
    }

    private void ApplyKickSubscriptionStatus(AravalsStream.Core.Settings.KickRelaySubscriptionStatus status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _loadedSettings.Relay.KickSubscriptionState = status.State;
            if (status.LastCheckedUtc is { } checkedAt) _loadedSettings.Relay.KickSubscriptionLastCheckedUtc = checkedAt;
            _loadedSettings.Relay.KickSubscriptionError = status.Error;
            _ = SaveSettingsAsync();
        });
    }

    private async Task ManageKickRelaySubscriptionsAsync(bool remove)
    {
        var account = _loadedSettings.KickAccount;
        if (account?.Connected != true) throw new InvalidOperationException("Connect a Kick account first.");
        if (!remove)
        {
            _loadedSettings.Relay.ManageKickSubscriptions = true;
            if (_kickRelaySubscriptions is null) ConfigureWebhookRelay();
            else _kickRelaySubscriptions.RequestCheck();
            await SaveSettingsAsync(); return;
        }
        _loadedSettings.Relay.ManageKickSubscriptions = false;
        if (_kickRelaySubscriptions is { } coordinator) { _kickRelaySubscriptions = null; await coordinator.DisposeAsync(); }
        await SaveSettingsAsync();
        var token = await new AravalsStream.Core.Kick.KickOAuthClient(_loadedSettings.KickOAuth, _secrets).GetValidAccessTokenAsync(account);
        ApplyKickSubscriptionStatus(await _relayClient.ManageKickSubscriptionsAsync(account.KickUserId, token, delete: true));
    }

    private static void AddBounded(ObservableCollection<ChatMessage> messages, ChatMessage message)
    {
        messages.Add(message);
        while (messages.Count > 500) messages.RemoveAt(0);
    }

    private void TickAlerts()
    {
        if (_alertEngine == null || _compositor is not SceneCompositor compositor) return;
        var now = DateTimeOffset.UtcNow;
        var chatSourceVisible = ViewModel.SelectedScene?.Sources.Any(s => s.Visible && s.Type == SourceType.ChatOverlay) == true;
        var chatVisible = chatSourceVisible && _unifiedChat.History.Any(m => ChatOverlayFilter.ShouldShow(m, _loadedSettings.Alerts.ChatOverlay, now));
        if (chatSourceVisible && (chatVisible || _chatOverlayWasVisible || _lastChatOverlayFrame == DateTimeOffset.MinValue) &&
            now - _lastChatOverlayFrame >= TimeSpan.FromSeconds(1))
        {
            try
            {
                var renderStart = Stopwatch.GetTimestamp();
                var history = _unifiedChat.History;
                compositor.SetOverlayRendered(SourceType.ChatOverlay, OutputMode.Horizontal, 650, 420,
                    pixels => ChatOverlayRenderer.RenderInto(history, _loadedSettings.Alerts.ChatOverlay, now, pixels));
                compositor.CopyOverlay(SourceType.ChatOverlay, OutputMode.Horizontal, OutputMode.Vertical);
                _performanceMetrics.Stage(PipelineStage.ChatRender, Stopwatch.GetTimestamp() - renderStart);
                if (_lastChatOverlayFrame == DateTimeOffset.MinValue) { HorizontalPreview.Refresh(); VerticalPreview.Refresh(); }
                _lastChatOverlayFrame = now;
                _chatOverlayWasVisible = chatVisible;
            }
            catch (Exception ex) { AppLog.Write("ChatOverlay", $"Frame skipped: {ex.Message}"); }
        }
        var alert = _alertEngine.Tick(now);
        if (alert == null && _lastRenderedAlert == null) return;
        bool changed = !ReferenceEquals(alert, _lastRenderedAlert);
        var animationFps = CurrentPerformanceProfile().PreviewFps switch
        {
            0 => 15,
            <= 15 => 15,
            >= 60 => 60,
            _ => 30
        };
        if (!changed && now - _lastAlertOverlayRender < TimeSpan.FromSeconds(1.0 / animationFps)) return;
        try
        {
            if (changed && alert != null) ApplyAlertLayout(alert.Definition);
            var renderStart = Stopwatch.GetTimestamp();
            compositor.SetOverlayRendered(SourceType.Alerts, OutputMode.Horizontal, 700, 180,
                pixels => AlertOverlayRenderer.RenderInto(alert, OutputMode.Horizontal, now, pixels));
            compositor.SetOverlayRendered(SourceType.Alerts, OutputMode.Vertical, 900, 240,
                pixels => AlertOverlayRenderer.RenderInto(alert, OutputMode.Vertical, now, pixels));
            _performanceMetrics.Stage(PipelineStage.AlertRender, Stopwatch.GetTimestamp() - renderStart);
            _lastRenderedAlert = alert;
            _lastAlertOverlayRender = now;
            if (changed) { HorizontalPreview.Refresh(); VerticalPreview.Refresh(); }
        }
        catch (Exception ex) { AppLog.Write("Alerts", $"Overlay frame skipped: {ex.Message}"); }
    }

    private void ApplyAlertLayout(AlertDefinition definition)
    {
        foreach (var source in ViewModel.SelectedScene?.Sources.Where(s => s.Type == SourceType.Alerts) ?? [])
        {
            Position(source.HorizontalTransform, 1920, 1080, Math.Clamp(definition.HorizontalWidth, 200, 1800), 180,
                definition.Position, definition.HorizontalX, definition.HorizontalY);
            Position(source.VerticalTransform, 1080, 1920, Math.Clamp(definition.VerticalWidth, 200, 1000), 240,
                definition.Position, definition.VerticalX, definition.VerticalY);
        }

        static void Position(SourceTransform t, int canvasW, int canvasH, int width, int height,
            AlertPosition position, int customX, int customY)
        {
            if (position == AlertPosition.Custom)
            { t.X = customX; t.Y = customY; return; }
            t.Width = width; t.Height = height;
            var margin = canvasH == 1920 ? 220 : 80;
            var left = canvasH == 1920 ? 40 : 40;
            var right = canvasH == 1920 ? 130 : 40;
            t.X = position is AlertPosition.TopLeft or AlertPosition.BottomLeft ? left
                : position is AlertPosition.TopRight or AlertPosition.BottomRight ? canvasW - width - right
                : left + (canvasW - left - right - width) / 2.0;
            t.Y = position is AlertPosition.TopLeft or AlertPosition.TopCenter or AlertPosition.TopRight ? margin
                : position is AlertPosition.BottomLeft or AlertPosition.BottomCenter or AlertPosition.BottomRight
                    ? canvasH - height - (canvasH == 1920 ? 370 : 60)
                : (canvasH - height) / 2.0;
        }
    }

    private void AlertSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new AlertSettingsWindow(_loadedSettings.Alerts,
            item => _alertEngine?.Enqueue(item)) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _unifiedChat.ConfigureChatLog(_loadedSettings.Alerts.SaveChatLog,
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AravalsStream", "chat-logs"));
            _ = SaveSettingsAsync();
        }
    }

    private void UpdateChatTargets()
    {
        var available = _unifiedChat.AvailableSendTargets();
        foreach (var item in ChatTargetBox.Items.OfType<ComboBoxItem>())
            item.IsEnabled = available.Contains(item.Content?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
        ChatSendBtn.IsEnabled = ChatTargetBox.SelectedItem is ComboBoxItem selected && selected.IsEnabled;
    }

    private void UpdateAlertStats()
    {
        ChatConnectionStats.Text = $"YouTube: {(_ytChatProvider?.IsConnected == true ? "CONNECTED" : "OFFLINE")}  " +
            $"Twitch: {(_twitchChatProvider?.IsConnected == true ? "CONNECTED" : "OFFLINE")}\n" +
            "Kick: WEBHOOK REQUIRED  " +
            $"Facebook: {(_facebookChatProvider?.IsConnected == true ? "POLLING" : "OFFLINE")}  " +
            "TikTok: UNSUPPORTED";
        AlertQueueStats.Text = $"Alerts queued: {_alertEngine?.QueuedCount ?? 0}  •  " +
            $"Last event: {(_activity.Count > 0 ? _activity[0] : "none")}";
    }
}
