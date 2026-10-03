using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using AravalsStream.Relay;
using AravalsStream.Core.Models;

var builder = WebApplication.CreateBuilder(args.Where(a => a != "--preflight").ToArray());
var preflightErrors = RelayPreflight.Validate(builder.Configuration, builder.Environment.IsDevelopment());
if (preflightErrors.Count > 0)
{
    foreach (var error in preflightErrors) Console.Error.WriteLine("RelayPreflight: " + error);
    Environment.ExitCode = 1; return;
}
if (args.Contains("--preflight")) { Console.WriteLine("RelayPreflight: PASS"); return; }
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.AddSingleton<RelayState>();
builder.Services.AddSingleton(sp => new KickSubscriptionService(sp.GetRequiredService<RelayState>(), builder.Configuration,
    new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })));
builder.Services.AddRateLimiter(options => options.AddPolicy("webhook", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
var app = builder.Build();
_ = app.Services.GetRequiredService<RelayState>();
var clock = Stopwatch.StartNew();
if (!app.Environment.IsDevelopment())
{
    var forwarded = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto };
    if (System.Net.IPAddress.TryParse(builder.Configuration["RELAY_TRUSTED_PROXY"], out var trustedProxy)) forwarded.KnownProxies.Add(trustedProxy);
    app.UseForwardedHeaders(forwarded);
    app.Use(async (context, next) =>
    {
        var allowedHosts = (builder.Configuration["RELAY_ALLOWED_HOSTS"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowedHosts.Length > 0 && !allowedHosts.Contains(context.Request.Host.Host, StringComparer.OrdinalIgnoreCase))
        { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        if (!context.Request.IsHttps) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        await next();
    });
}
else
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps && (context.Connection.RemoteIpAddress is not { } remoteIp || !System.Net.IPAddress.IsLoopback(remoteIp)))
        { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        await next();
    });
}
app.UseRateLimiter();
app.MapGet("/health", (RelayState state) => Results.Ok(new { status = "ok", version = "0.20.1-beta", uptimeSeconds = (long)clock.Elapsed.TotalSeconds, connections = state.ConnectionCount }));
app.MapGet("/api/v1/status", (RelayState state) => Results.Ok(new { status = "available", version = "0.20.1-beta", connections = state.ConnectionCount }));

app.MapPost("/api/v1/enroll", async (HttpRequest req, RelayState state) =>
{
    var dto = await ReadJson<EnrollRequest>(req); if (dto is null) return Results.BadRequest();
    var result = state.Enroll(dto.Code, dto.Channels ?? []);
    return result is null ? Results.Unauthorized() : Results.Ok(new { installationId = result.Value.InstallationId, refreshToken = result.Value.RefreshToken, accessToken = result.Value.AccessToken, expiresInSeconds = 300 });
}).RequireRateLimiting("webhook");

app.MapGet("/api/v1/kick/subscriptions", (HttpRequest req, RelayState state) =>
{
    try { return Results.Ok(state.GetKickSubscription(Bearer(req), req.Query["channelId"].ToString())); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
}).RequireRateLimiting("webhook");
app.MapPost("/api/v1/kick/subscriptions/reconcile", async (HttpRequest req, KickSubscriptionService service) =>
{
    var dto = await ReadJson<KickSubscriptionRequest>(req); if (dto is null || string.IsNullOrEmpty(dto.ChannelId)) return Results.BadRequest();
    try { return Results.Ok(await service.ReconcileAsync(Bearer(req), dto.ChannelId, dto.KickAccessToken, ct: req.HttpContext.RequestAborted)); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
}).RequireRateLimiting("webhook");
app.MapPost("/api/v1/kick/subscriptions/delete", async (HttpRequest req, KickSubscriptionService service) =>
{
    var dto = await ReadJson<KickSubscriptionRequest>(req); if (dto is null || string.IsNullOrEmpty(dto.ChannelId)) return Results.BadRequest();
    try { return Results.Ok(await service.ReconcileAsync(Bearer(req), dto.ChannelId, dto.KickAccessToken, delete: true, ct: req.HttpContext.RequestAborted)); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
}).RequireRateLimiting("webhook");
app.MapPost("/api/v1/session/refresh", async (HttpRequest req, RelayState state) =>
{
    var dto = await ReadJson<RefreshRequest>(req); if (dto is null) return Results.BadRequest();
    var result = state.Refresh(dto.InstallationId, dto.RefreshToken);
    return result is null ? Results.Unauthorized() : Results.Ok(new { accessToken = result.Value.AccessToken, expiresAt = result.Value.ExpiresAt });
}).RequireRateLimiting("webhook");
app.MapPost("/api/v1/session/revoke", async (HttpRequest req, RelayState state) =>
{
    var dto = await ReadJson<RefreshRequest>(req); if (dto is null) return Results.BadRequest();
    return state.Revoke(dto.InstallationId, dto.RefreshToken) ? Results.NoContent() : Results.Unauthorized();
}).RequireRateLimiting("webhook");

app.MapGet("/webhooks/facebook", (HttpRequest req, IConfiguration config) =>
{
    var mode = req.Query["hub.mode"].ToString(); var token = req.Query["hub.verify_token"].ToString();
    var challenge = req.Query["hub.challenge"].ToString();
    if (mode != "subscribe" || string.IsNullOrEmpty(challenge) || !RelaySecurity.FixedTimeSecretEquals(token, config["FACEBOOK_WEBHOOK_VERIFY_TOKEN"]))
        return Results.Unauthorized();
    return Results.Text(challenge, "text/plain");
});

app.MapPost("/webhooks/kick", async (HttpRequest req, IConfiguration config, RelayState state, ILoggerFactory logs) =>
{
    var log = logs.CreateLogger("RelayWebhook");
    var body = await ReadBody(req); if (body is null) return Results.StatusCode(413);
    var messageId = req.Headers["Kick-Event-Message-Id"].ToString();
    var timestamp = req.Headers["Kick-Event-Message-Timestamp"].ToString();
    var signature = req.Headers["Kick-Event-Signature"].ToString();
    if (!RelaySecurity.VerifyKickSignature(body, messageId, timestamp, signature, RelaySecurity.KickPublicKey(config), DateTimeOffset.UtcNow))
    { log.LogWarning("WebhookRejected Provider={Provider} Reason={Reason}", "Kick", "signature_or_timestamp"); return Results.Unauthorized(); }
    var type = req.Headers["Kick-Event-Type"].ToString();
    try
    {
        using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return Results.BadRequest();
        var channel = ReadKickChannel(doc.RootElement);
        if (channel.Length == 0 || type.Length is 0 or > 100) return Results.BadRequest();
        if (type is not ("chat.message.sent" or "channel.followed" or "channel.subscription.new" or "channel.subscription.renewal" or "channel.subscription.gifts")) return Results.NoContent();
        if (state.IsDuplicate("kick", messageId, DateTimeOffset.UtcNow)) { log.LogInformation("DuplicateEventIgnored Provider={Provider}", "Kick"); return Results.Ok(); }
        var normalized = NormalizeKick(doc.RootElement, type);
        var evt = new RelayEvent(messageId, "kick", type, ReadOccurred(doc.RootElement), DateTimeOffset.UtcNow, channel, "", 1, normalized);
        var delivered = state.Deliver(evt with { InstallationId = "" });
        log.LogInformation("WebhookVerified Provider={Provider} EventType={EventType} Delivered={Delivered}", "Kick", type, delivered);
        return Results.Ok();
    }
    catch (JsonException) { log.LogWarning("WebhookRejected Provider={Provider} Reason={Reason}", "Kick", "malformed_json"); return Results.BadRequest(); }
}).RequireRateLimiting("webhook");

app.MapPost("/webhooks/facebook", async (HttpRequest req, IConfiguration config, RelayState state, ILoggerFactory logs) =>
{
    var log = logs.CreateLogger("RelayWebhook"); var body = await ReadBody(req); if (body is null) return Results.StatusCode(413);
    if (!RelaySecurity.VerifyFacebookSignature(body, req.Headers["X-Hub-Signature-256"].ToString(), config["FACEBOOK_APP_SECRET"]))
    { log.LogWarning("WebhookRejected Provider={Provider} Reason={Reason}", "Facebook", "signature"); return Results.Unauthorized(); }
    try
    {
        using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("object", out var obj) || obj.ValueKind != JsonValueKind.String || obj.GetString() != "page" ||
            !doc.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) return Results.BadRequest();
        var receivedAt = DateTimeOffset.UtcNow; var deliveries = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var page = PropertyString(entry, "id");
            if (page.Length == 0 || !entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) continue;
            var occurredAt = ReadFacebookOccurred(entry, receivedAt);
            foreach (var change in changes.EnumerateArray())
            {
                var field = PropertyString(change, "field");
                if (field is not ("feed" or "live_videos")) continue;
                var canonical = Encoding.UTF8.GetBytes(page + "." + occurredAt.ToUnixTimeSeconds() + "." + JsonSerializer.Serialize(change));
                var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(canonical));
                if (state.IsDuplicate("facebook", id, receivedAt)) continue;
                var payload = JsonSerializer.SerializeToElement(new { field, value = change.TryGetProperty("value", out var value) ? value : default });
                if (state.Deliver(new RelayEvent(id, "facebook", field, occurredAt, receivedAt, page, "", 1, payload))) deliveries++;
            }
        }
        log.LogInformation("WebhookVerified Provider={Provider} Delivered={Delivered}", "Facebook", deliveries);
        return Results.Ok();
    }
    catch (JsonException) { log.LogWarning("WebhookRejected Provider={Provider} Reason={Reason}", "Facebook", "malformed_json"); return Results.BadRequest(); }
}).RequireRateLimiting("webhook");

app.Map("/ws", async (HttpContext context, RelayState state, ILoggerFactory logs) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync(); var log = logs.CreateLogger("RelaySocket");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var authText = await ReceiveText(socket, timeout.Token);
    if (authText is null) { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Authentication required", CancellationToken.None); return; }
    string? installation; string[]? channels;
    try { var auth = JsonSerializer.Deserialize<SocketAuth>(authText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); installation = state.ValidateAccess(auth?.AccessToken); channels = auth?.Channels; }
    catch (JsonException) { installation = null; channels = null; }
    if (installation is null || channels is null || channels.Length > 32 || channels.Any(c => !ValidChannel(c)))
    { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Authentication failed", CancellationToken.None); return; }
    foreach (var route in channels)
    {
        var split = route.Split(':', 2);
        if (split.Length != 2 || !state.TryOwnChannel(installation, split[0], split[1]))
        { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Channel ownership conflict", CancellationToken.None); return; }
    }
    if (!state.TryAttach(installation, socket, out var reader) || reader is null)
    { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Connection limit reached", CancellationToken.None); return; }
    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "authenticated" }), WebSocketMessageType.Text, true, context.RequestAborted);
    log.LogInformation("RelayConnected InstallationId={InstallationId}", installation);
    using var life = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var sessionExpiry = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromMinutes(5), life.Token);
        if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Session expired", CancellationToken.None);
    }, life.Token);
    var send = Task.Run(async () =>
    {
        await foreach (var evt in reader.ReadAllAsync(life.Token))
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(evt), WebSocketMessageType.Text, true, life.Token);
        if (socket.State == WebSocketState.Open)
            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Session revoked", CancellationToken.None);
    }, life.Token);
    try { while (socket.State == WebSocketState.Open)
        {
            var frame = await ReceiveText(socket, context.RequestAborted);
            if (frame is null) break;
            // Refresh the short-lived access token over the authenticated TLS socket.
            SocketRefresh? refresh; try { refresh = JsonSerializer.Deserialize<SocketRefresh>(frame, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); } catch (JsonException) { break; }
            if (refresh?.AccessToken is null || state.ValidateAccess(refresh.AccessToken) != installation) break;
        }
    }
    catch (OperationCanceledException) { }
    finally { life.Cancel(); state.Detach(installation, socket); try { await send; await sessionExpiry; } catch { } }
}).RequireRateLimiting("webhook");

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.Run();

static async Task<byte[]?> ReadBody(HttpRequest req)
{
    if (req.ContentLength is > 65536) return null;
    using var output = new MemoryStream(); var buffer = new byte[8192];
    while (true) { var n = await req.Body.ReadAsync(buffer); if (n == 0) break; if (output.Length + n > 65536) return null; output.Write(buffer, 0, n); }
    return output.ToArray();
}
static async Task<T?> ReadJson<T>(HttpRequest req) where T : class
{
    var body = await ReadBody(req); if (body is null) return null;
    try { return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { MaxDepth = 8, PropertyNameCaseInsensitive = true }); } catch (JsonException) { return null; }
}
static string PropertyString(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
static string ReadKickChannel(JsonElement root)
{
    foreach (var container in new[] { "broadcaster", "channel" })
        if (root.TryGetProperty(container, out var c) && c.ValueKind == JsonValueKind.Object)
            foreach (var prop in new[] { "user_id", "id" }) if (c.TryGetProperty(prop, out var id)) return id.ToString();
    return "";
}
static JsonElement NormalizeKick(JsonElement root, string type)
{
    var actorName = ""; var actorId = ""; var content = "";
    foreach (var name in new[] { "sender", "follower", "subscriber", "gifter" })
        if (root.TryGetProperty(name, out var actor) && actor.ValueKind == JsonValueKind.Object)
        { actorName = PropertyString(actor, "username"); actorId = actor.TryGetProperty("user_id", out var uid) ? uid.ToString() : ""; break; }
    content = PropertyString(root, "content");
    return JsonSerializer.SerializeToElement(new { actorId, actorName, message = content, eventType = type });
}
static DateTimeOffset ReadOccurred(JsonElement root) => DateTimeOffset.TryParse(PropertyString(root, "created_at"), out var time) ? time : DateTimeOffset.UtcNow;
static DateTimeOffset ReadFacebookOccurred(JsonElement entry, DateTimeOffset fallback)
{
    if (!entry.TryGetProperty("time", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds)) return fallback;
    try { return DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { return fallback; }
}
static bool ValidChannel(string route) { var p = route.Split(':', 2); return p.Length == 2 && p[0] is "kick" or "facebook" && p[1].Length is > 0 and <= 200; }
static async Task<string?> ReceiveText(WebSocket socket, CancellationToken ct)
{
    var buffer = new byte[4096]; using var ms = new MemoryStream();
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        if (result.MessageType != WebSocketMessageType.Text || ms.Length + result.Count > 4096) return null;
        ms.Write(buffer, 0, result.Count); if (result.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
    }
}
static string Bearer(HttpRequest request)
{
    var header = request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
}
record EnrollRequest(string? Code, string[]? Channels);
record RefreshRequest(string InstallationId, string? RefreshToken);
record SocketAuth(string? AccessToken, string[]? Channels);
record SocketRefresh(string? AccessToken);
record KickSubscriptionRequest(string ChannelId, string KickAccessToken);
