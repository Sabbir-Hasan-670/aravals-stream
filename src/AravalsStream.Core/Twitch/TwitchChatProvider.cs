using AravalsStream.Core.Accounts;
using AravalsStream.Core.Chat;
using AravalsStream.Core.Models;

namespace AravalsStream.Core.Twitch;

public sealed class TwitchChatProvider : IChatProvider
{
    private readonly TwitchAccount _account;
    private readonly TwitchOAuthClient _oauth;
    private readonly TwitchApiClient _api;
    private TwitchEventService? _events;
    public string Platform => "Twitch";
    public bool IsConnected => _events?.IsConnected == true;
    public string? StatusMessage { get; private set; } = "Offline";
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;

    public TwitchChatProvider(TwitchAccount account, TwitchOAuthClient oauth, TwitchApiClient api)
    { _account = account; _oauth = oauth; _api = api; }

    public async Task StartAsync(string liveChatId, CancellationToken ct = default)
    {
        if (_events != null) return;
        if (liveChatId != _account.UserId) throw new InvalidOperationException("Twitch chat target does not match connected account.");
        var service = new TwitchEventService(_account, _oauth, _api);
        service.NotificationReceived += notification =>
        {
            var message = TwitchEventMapper.Map(notification);
            if (message != null) MessageReceived?.Invoke(message);
        };
        service.StatusChanged += status => { StatusMessage = status; StatusChanged?.Invoke(status); };
        _events = service;
        try { await service.StartAsync(ct); }
        catch { _events = null; await service.DisposeAsync(); throw; }
    }

    public async Task<bool> SendMessageAsync(string text, CancellationToken ct = default)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(text)) return false;
        var token = await _oauth.GetValidAccessTokenAsync(_account, ct);
        return await _api.SendChatMessageAsync(token, _account.UserId, _account.UserId, text.Trim(), ct);
    }

    public async Task StopAsync()
    {
        if (_events != null) { await _events.DisposeAsync(); _events = null; }
        StatusMessage = "Offline"; StatusChanged?.Invoke("Offline");
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
