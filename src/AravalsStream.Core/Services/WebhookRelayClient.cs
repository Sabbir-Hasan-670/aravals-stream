using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AravalsStream.Core.Models;
using AravalsStream.Core.Settings;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AravalsStream.Core.Services;

public enum RelayConnectionState { Disconnected, Connecting, Connected, Reconnecting, Error }

public sealed class WebhookRelayClient : IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource _stop = new();
    private Task? _worker;
    private string _url = ""; private string _installationId = ""; private string _refreshToken = ""; private string[] _channels = [];
    private int _reconnectCount; private long _received; private long _rejected;
    public RelayConnectionState State { get; private set; } = RelayConnectionState.Disconnected;
    public int ReconnectCount => Volatile.Read(ref _reconnectCount);
    public long EventsReceived => Interlocked.Read(ref _received);
    public long EventsRejected => Interlocked.Read(ref _rejected);
    public DateTimeOffset? LastConnected { get; private set; }
    public DateTimeOffset? LastEvent { get; private set; }
    public event Action<RelayConnectionState>? StateChanged;
    public event Action<RelayEvent>? EventReceived;

    public void Start(string relayUrl, string installationId, string refreshToken, IEnumerable<string> channels)
    {
        Stop();
        if (!Uri.TryCreate(relayUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            (uri.Scheme == "http" && !IsLocal(uri)) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || string.IsNullOrWhiteSpace(installationId) || string.IsNullOrWhiteSpace(refreshToken))
        { SetState(RelayConnectionState.Error); return; }
        _url = relayUrl.TrimEnd('/'); _installationId = installationId; _refreshToken = refreshToken; _channels = channels.Take(32).ToArray();
        _worker = Task.Run(() => RunAsync(_stop.Token));
    }

    public void Stop()
    {
        _stop.Cancel();
        _stop = new CancellationTokenSource();
        SetState(RelayConnectionState.Disconnected);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetState(attempt == 0 ? RelayConnectionState.Connecting : RelayConnectionState.Reconnecting);
            try
            {
                var access = await RefreshAccessAsync(ct);
                using var socket = new ClientWebSocket();
                var endpoint = new Uri(_url.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase) + "/ws");
                await socket.ConnectAsync(endpoint, ct);
                var auth = JsonSerializer.SerializeToUtf8Bytes(new { accessToken = access, channels = _channels });
                await socket.SendAsync(auth, WebSocketMessageType.Text, true, ct);
                var acknowledgement = new byte[512];
                var authResult = await socket.ReceiveAsync(acknowledgement, ct);
                if (authResult.MessageType != WebSocketMessageType.Text || !authResult.EndOfMessage ||
                    !JsonDocument.Parse(acknowledgement.AsMemory(0, authResult.Count)).RootElement.TryGetProperty("type", out var authType) ||
                    authType.GetString() != "authenticated") throw new UnauthorizedAccessException("Relay rejected the Desktop session.");
                SetState(RelayConnectionState.Connected); LastConnected = DateTimeOffset.UtcNow; attempt = 0;
                await ReceiveLoopAsync(socket, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { attempt++; Interlocked.Increment(ref _reconnectCount); SetState(RelayConnectionState.Reconnecting); }
            if (!ct.IsCancellationRequested)
            {
                var exponential = Math.Min(60, Math.Pow(2, Math.Min(attempt, 6)));
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * exponential * 1000), ct);
            }
        }
        SetState(RelayConnectionState.Disconnected);
    }

    private async Task<string> RefreshAccessAsync(CancellationToken ct)
    {
        var content = JsonSerializer.Serialize(new { installationId = _installationId, refreshToken = _refreshToken });
        using var response = await _http.PostAsync(_url + "/api/v1/session/refresh", new StringContent(content, Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("accessToken").GetString() ?? throw new InvalidDataException("Relay access token missing.");
    }

    public async Task<KickRelaySubscriptionStatus> ManageKickSubscriptionsAsync(string channelId, string kickAccessToken,
        bool delete = false, CancellationToken ct = default)
    {
        if (State != RelayConnectionState.Connected) throw new InvalidOperationException("Relay must be connected to manage subscriptions.");
        var access = await RefreshAccessAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, _url + "/api/v1/kick/subscriptions/" + (delete ? "delete" : "reconcile"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Content = JsonContent.Create(new { channelId, kickAccessToken });
        using var subscriptionTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); subscriptionTimeout.CancelAfter(TimeSpan.FromSeconds(50));
        // Separate timeout permits bounded provider discovery retries without blocking the socket/media worker.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.SendAsync(request, subscriptionTimeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<KickRelaySubscriptionStatus>(cancellationToken: subscriptionTimeout.Token)
            ?? throw new InvalidDataException("Missing Kick subscription status.");
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream(); WebSocketReceiveResult result;
            do { result = await socket.ReceiveAsync(buffer, ct); if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text || ms.Length + result.Count > 65536) { Interlocked.Increment(ref _rejected); throw new InvalidDataException("Invalid relay frame."); }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            try { var evt = JsonSerializer.Deserialize<RelayEvent>(ms.ToArray()); if (evt is null || evt.SchemaVersion != 1 || evt.EventId.Length > 200) { Interlocked.Increment(ref _rejected); continue; }
                Interlocked.Increment(ref _received); LastEvent = DateTimeOffset.UtcNow; EventReceived?.Invoke(evt); }
            catch (JsonException) { Interlocked.Increment(ref _rejected); }
        }
    }
    private void SetState(RelayConnectionState value) { State = value; try { StateChanged?.Invoke(value); } catch { } }
    private static bool IsLocal(Uri uri) => uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    public async ValueTask DisposeAsync() { _stop.Cancel(); if (_worker is not null) try { await _worker; } catch { } _stop.Dispose(); _http.Dispose(); }
}
