using AravalsStream.Core.Accounts;
using AravalsStream.Core.Chat;
using AravalsStream.Core.Models;
using System.Text.Json;

namespace AravalsStream.Core.Kick;

public sealed class KickChatProvider : IChatProvider
{
    private readonly KickAccount _account;
    private readonly KickOAuthClient _oauth;
    private readonly KickApiClient _api;
    public KickChatProvider(KickAccount account, KickOAuthClient oauth, KickApiClient api)
    { _account = account; _oauth = oauth; _api = api; }
    public string Platform => "Kick";
    public bool IsConnected { get; private set; }
    public string? StatusMessage { get; private set; } = "Offline";
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;

    // Only a future signature-verified public webhook receiver may call this.
    internal void AcceptVerifiedEvent(string eventType, JsonElement payload)
    {
        var message = KickEventMapper.Map(eventType, payload);
        if (message != null) MessageReceived?.Invoke(message);
    }

    public async Task StartAsync(string liveChatId, CancellationToken ct = default)
    {
        if (liveChatId != _account.KickUserId) throw new InvalidOperationException("Kick chat target does not match connected account.");
        await _oauth.GetValidAccessTokenAsync(_account, ct);
        IsConnected = true;
        StatusMessage = "Send available; receive requires public webhook";
        StatusChanged?.Invoke(StatusMessage);
    }

    public async Task<bool> SendMessageAsync(string text, CancellationToken ct = default)
    {
        if (!IsConnected || !long.TryParse(_account.KickUserId, out var userId)) return false;
        var token = await _oauth.GetValidAccessTokenAsync(_account, ct);
        return await _api.SendChatMessageAsync(token, userId, text.Trim(), ct);
    }

    public Task StopAsync()
    {
        IsConnected = false;
        StatusMessage = "Offline";
        StatusChanged?.Invoke(StatusMessage);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { _ = StopAsync(); return ValueTask.CompletedTask; }
}
