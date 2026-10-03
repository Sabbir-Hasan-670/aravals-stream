using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AravalsStream.Core.Accounts;

namespace AravalsStream.Core.Twitch;

public sealed class TwitchEventService : IAsyncDisposable
{
    private readonly TwitchAccount _account;
    private readonly TwitchOAuthClient _oauth;
    private readonly TwitchApiClient _api;
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _seenOrder = [];
    private CancellationTokenSource? _stop;
    private Task? _runner;
    public bool IsConnected { get; private set; }
    public event Action<JsonElement>? NotificationReceived;
    public event Action<string>? StatusChanged;

    public TwitchEventService(TwitchAccount account, TwitchOAuthClient oauth, TwitchApiClient api)
    { _account = account; _oauth = oauth; _api = api; }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_stop != null) return Task.CompletedTask;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runner = Task.Run(() => RunAsync(_stop.Token));
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var uri = new Uri("wss://eventsub.wss.twitch.tv/ws");
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            var serverRequestedReconnect = false;
            try
            {
                StatusChanged?.Invoke(attempt == 0 ? "Connecting" : "Reconnecting");
                await socket.ConnectAsync(uri, ct);
                var transferred = uri.Host == "eventsub.wss.twitch.tv" && uri.Query.Length > 0;
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var jsonText = await ReadMessageAsync(socket, ct);
                    if (jsonText == null) break;
                    using var json = JsonDocument.Parse(jsonText);
                    var root = json.RootElement;
                    var metadata = root.GetProperty("metadata");
                    var type = metadata.GetProperty("message_type").GetString();
                    if (type == "session_welcome")
                    {
                        var sessionId = root.GetProperty("payload").GetProperty("session").GetProperty("id").GetString() ?? "";
                        if (!transferred) await SubscribeAsync(sessionId, ct);
                        IsConnected = true; StatusChanged?.Invoke("Connected");
                        attempt = 0;
                    }
                    else if (type == "notification")
                    {
                        var id = metadata.GetProperty("message_id").GetString() ?? "";
                        if (Remember(id)) NotificationReceived?.Invoke(root.Clone());
                    }
                    else if (type == "session_reconnect")
                    {
                        var address = root.GetProperty("payload").GetProperty("session").GetProperty("reconnect_url").GetString();
                        if (Uri.TryCreate(address, UriKind.Absolute, out var reconnect)) uri = reconnect;
                        serverRequestedReconnect = true;
                        break;
                    }
                    else if (type == "revocation") StatusChanged?.Invoke("Authorization revoked");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception)
            {
                StatusChanged?.Invoke("Reconnecting");
            }
            IsConnected = false;
            if (ct.IsCancellationRequested) break;
            if (!serverRequestedReconnect)
            {
                uri = new Uri("wss://eventsub.wss.twitch.tv/ws");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt++, 5)))), ct);
            }
        }
        IsConnected = false;
        StatusChanged?.Invoke("Offline");
    }

    private async Task SubscribeAsync(string sessionId, CancellationToken ct)
    {
        var token = await _oauth.GetValidAccessTokenAsync(_account, ct);
        var id = _account.UserId;
        await _api.CreateEventSubscriptionAsync(token, "channel.chat.message", "1", new { broadcaster_user_id = id, user_id = id }, sessionId, ct);
        var optional = new (string Type, object Condition)[]
        {
            ("channel.cheer", new { broadcaster_user_id = id }),
            ("channel.subscribe", new { broadcaster_user_id = id }),
            ("channel.subscription.gift", new { broadcaster_user_id = id }),
            ("channel.raid", new { to_broadcaster_user_id = id })
        };
        foreach (var (type, condition) in optional)
        {
            try { await _api.CreateEventSubscriptionAsync(token, type, "1", condition, sessionId, ct); }
            catch (HttpRequestException) { /* Optional event permission or quota unavailable. Chat remains connected. */ }
        }
    }

    private bool Remember(string id)
    {
        if (id.Length == 0) return true;
        if (!_seen.Add(id)) return false;
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > 1000) _seen.Remove(_seenOrder.Dequeue());
        return true;
    }

    private static async Task<string?> ReadMessageAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            bytes.Write(buffer, 0, result.Count);
            if (bytes.Length > 1_048_576) throw new InvalidOperationException("Twitch EventSub message is too large.");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }

    public async Task StopAsync()
    {
        if (_stop == null) return;
        _stop.Cancel();
        if (_runner != null) { try { await _runner; } catch (OperationCanceledException) { } }
        _stop.Dispose(); _stop = null; _runner = null;
        IsConnected = false;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
