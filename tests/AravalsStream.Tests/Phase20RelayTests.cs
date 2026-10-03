using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using AravalsStream.Core.Chat;
using AravalsStream.Core.Alerts;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using AravalsStream.Relay;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace AravalsStream.Tests;

public sealed class Phase20RelayTests
{
    private readonly ITestOutputHelper _output;
    public Phase20RelayTests(ITestOutputHelper output) => _output = output;

    private static IConfiguration Configuration(string path) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["RELAY_TOKEN_SIGNING_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["RELAY_ENROLLMENT_CREDENTIALS_JSON"] = JsonSerializer.Serialize(new[]
        {
            new { code = "test-enrollment-code-0123456789", channels = new[] { "kick:101", "facebook:page-77" } },
            new { code = "other-enrollment-code-0123456789", channels = new[] { "kick:202" } }
        }),
        ["RELAY_STORAGE_PATH"] = path
    }).Build();
    private static RelayState State() => new(Configuration(Path.Combine(Path.GetTempPath(), "aravals-relay-" + Guid.NewGuid().ToString("N") + ".json")));

    [Fact]
    public void FacebookSignatureAcceptsValidAndRejectsInvalid()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"object\":\"page\"}"); const string secret = "test-facebook-secret";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
        Assert.True(RelaySecurity.VerifyFacebookSignature(body, signature, secret));
        Assert.False(RelaySecurity.VerifyFacebookSignature(body, "sha256=" + new string('0', 64), secret));
        Assert.False(RelaySecurity.VerifyFacebookSignature(body, signature, null));
    }

    [Fact]
    public void KickSignatureValidatesSignedRawBodyAndRejectsTamperAndReplay()
    {
        using var rsa = RSA.Create(2048); var pem = rsa.ExportSubjectPublicKeyInfoPem();
        byte[] body = Encoding.UTF8.GetBytes("{\"content\":\"hello\"}"); var id = "evt-1";
        var stamp = DateTimeOffset.UtcNow.ToString("O"); var signed = Encoding.UTF8.GetBytes($"{id}.{stamp}.").Concat(body).ToArray();
        var sig = Convert.ToBase64String(rsa.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.True(RelaySecurity.VerifyKickSignature(body, id, stamp, sig, pem, DateTimeOffset.UtcNow));
        Assert.False(RelaySecurity.VerifyKickSignature(Encoding.UTF8.GetBytes("{}"), id, stamp, sig, pem, DateTimeOffset.UtcNow));
        Assert.False(RelaySecurity.VerifyKickSignature(body, id, DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"), sig, pem, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EnrollmentAccessRefreshAndRevocationAreInstallationScoped()
    {
        var state = State(); var enrolled = state.Enroll("test-enrollment-code-0123456789"); Assert.NotNull(enrolled);
        var (id, refresh, access) = enrolled!.Value;
        Assert.Equal(id, state.ValidateAccess(access));
        Assert.Null(state.ValidateAccess("invalid.token"));
        Assert.NotNull(state.Refresh(id, refresh));
        Assert.True(state.Revoke(id, refresh));
        Assert.Null(state.ValidateAccess(access));
        Assert.Null(state.Refresh(id, refresh));
        Assert.Null(state.Enroll("test-enrollment-code-0123456789"));
    }

    [Fact]
    public void RoutingIsOwnedAndEventsReachOnlyTheMatchingInstallation()
    {
        var state = State(); var a = state.Enroll("test-enrollment-code-0123456789")!.Value;
        Assert.Null(state.Enroll("other-enrollment-code-0123456789", ["kick:999"]));
        var b = state.Enroll("other-enrollment-code-0123456789", ["kick:202"])!.Value;
        Assert.True(state.TryOwnChannel(a.InstallationId, "kick", "101"));
        Assert.True(state.TryOwnChannel(b.InstallationId, "kick", "202"));
        Assert.False(state.TryOwnChannel(a.InstallationId, "kick", "202"));
        Assert.True(state.TryAttach(a.InstallationId, out var readA)); Assert.True(state.TryAttach(b.InstallationId, out var readB));
        var payload = JsonSerializer.SerializeToElement(new { eventType = "channel.followed" });
        Assert.True(state.Deliver(new RelayEvent("id-1", "kick", "channel.followed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "101", "", 1, payload)));
        Assert.True(readA!.TryRead(out var got)); Assert.Equal(a.InstallationId, got!.InstallationId);
        Assert.False(readB!.TryRead(out _));
        Assert.False(state.Deliver(new RelayEvent("id-2", "kick", "channel.followed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "missing", "", 1, payload)));
    }

    [Fact]
    public void ProviderEventDeduplicationExpiresAndRemainsBounded()
    {
        var state = State(); var now = DateTimeOffset.UtcNow;
        Assert.False(state.IsDuplicate("kick", "one", now));
        Assert.True(state.IsDuplicate("kick", "one", now.AddSeconds(1)));
        Assert.False(state.IsDuplicate("kick", "one", now.AddHours(25)));
        for (var i = 0; i < 10005; i++) state.IsDuplicate("kick", "bounded-" + i, now.AddSeconds(i));
        Assert.False(state.IsDuplicate("kick", "bounded-0", now.AddSeconds(10010)));
    }

    [Fact]
    public void SessionAndChannelOwnershipSurviveRelayRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "aravals-relay-persist-" + Guid.NewGuid().ToString("N") + ".json");
        var config = Configuration(path); var first = new RelayState(config);
        var enrolled = first.Enroll("test-enrollment-code-0123456789")!.Value;
        Assert.True(first.TryOwnChannel(enrolled.InstallationId, "facebook", "page-77"));
        var restarted = new RelayState(config);
        Assert.Equal(enrolled.InstallationId, restarted.ValidateAccess(enrolled.AccessToken));
        Assert.NotNull(restarted.Refresh(enrolled.InstallationId, enrolled.RefreshToken));
        Assert.False(restarted.TryOwnChannel(Guid.NewGuid().ToString("N"), "facebook", "page-77"));
        Assert.Null(restarted.Enroll("test-enrollment-code-0123456789"));
        File.Delete(path);
    }

    [Fact]
    public async Task UnifiedEventBusDeduplicatesRelayEvents()
    {
        await using var chat = new UnifiedChatService(); var count = 0;
        chat.EventReceived += _ => count++;
        var item = new StreamEvent { Id = "relay-1", Platform = "Kick", EventType = StreamEventType.Follow, ActorName = "viewer" };
        Assert.True(chat.PublishRelayEvent(item)); Assert.False(chat.PublishRelayEvent(item));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task VerifiedKickWebhookUsesExistingUnifiedChatAndAlertEventPath()
    {
        await using var chat = new UnifiedChatService();
        var alerts = new AlertEngine(new AlertSettings());
        var observed = new TaskCompletionSource<StreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.EventReceived += evt => observed.TrySetResult(evt);
        Assert.True(chat.PublishVerifiedWebhook(new ChatMessage
        {
            Id = "verified-follow-1", RawProviderMessageId = "verified-follow-1", Platform = "Kick",
            AuthorId = "user-4", AuthorName = "follower", Text = "Followed the channel", MessageType = ChatMessageType.Follow
        }));
        var evt = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StreamEventType.Follow, evt.EventType);
        Assert.Equal("follower", evt.ActorName);
        Assert.True(alerts.Enqueue(evt));
        Assert.Equal(evt.Id, alerts.Tick(DateTimeOffset.UtcNow.AddSeconds(1))?.Event.Id);
        Assert.False(chat.PublishVerifiedWebhook(new ChatMessage { Id = "unsupported", Platform = "YouTube" }));
    }

    [Fact]
    public async Task RelayClientRejectsInsecureNonLocalUrlWithoutThrowing()
    {
        await using var client = new WebhookRelayClient();
        client.Start("http://relay.example", "install", "refresh", []);
        Assert.Equal(RelayConnectionState.Error, client.State);
    }

    [Fact]
    public async Task LocalRelayWebhookToDesktopToUnifiedBusSurvivesRelayRestart()
    {
        var relayDll = Path.Combine(AppContext.BaseDirectory, "AravalsStream.Relay.dll");
        Assert.True(File.Exists(relayDll), $"Relay test host missing: {relayDll}");
        using var rsa = RSA.Create(2048);
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start(); var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        var baseUrl = $"http://127.0.0.1:{port}";
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem(); const string enrollment = "e2e-enrollment-code-is-long-enough";
        const string fbSecret = "local-meta-test-secret"; const string fbVerify = "meta-verify-test-token";
        var storage = Path.Combine(Path.GetTempPath(), "aravals-relay-e2e-" + Guid.NewGuid().ToString("N") + ".json");
        Process? relay = null;
        using var http = new HttpClient();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Process StartRelay()
        {
            var info = new ProcessStartInfo("dotnet", $"\"{relayDll}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; info.Environment["ASPNETCORE_URLS"] = baseUrl;
            info.Environment["RELAY_TOKEN_SIGNING_KEY"] = token;
            info.Environment["RELAY_STORAGE_PATH"] = storage;
            info.Environment["RELAY_ENROLLMENT_CREDENTIALS_JSON"] = JsonSerializer.Serialize(new[] { new { code = enrollment, channels = new[] { "kick:101", "facebook:page-1" } } });
            info.Environment["KICK_WEBHOOK_PUBLIC_KEY_PEM"] = publicPem;
            info.Environment["KICK_WEBHOOK_PUBLIC_KEY_FILE"] = ""; info.Environment["KICK_SUBSCRIPTIONS_ENABLED"] = "false";
            info.Environment["FACEBOOK_WEBHOOKS_ENABLED"] = "false";
            info.Environment["FACEBOOK_WEBHOOK_VERIFY_TOKEN"] = fbVerify; info.Environment["FACEBOOK_APP_SECRET"] = fbSecret;
            var process = Process.Start(info)!; _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync(); return process;
        }
        async Task WaitHealthAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            for (var i = 0; i < 100; i++)
            {
                if (relay is { HasExited: true }) throw new InvalidOperationException("Local Relay exited before becoming healthy.");
                try { if ((await http.GetAsync(baseUrl + "/health")).IsSuccessStatusCode) return; } catch { }
                await Task.Delay(100);
            }
            throw new TimeoutException("Local Relay did not become healthy.");
        }
        async Task WaitConnectionAsync(int expected)
        {
            for (var i = 0; i < 100; i++)
            {
                try
                {
                    using var status = await http.GetAsync(baseUrl + "/api/v1/status");
                    using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
                    if (json.RootElement.GetProperty("connections").GetInt32() == expected) return;
                }
                catch { }
                await Task.Delay(50);
            }
            throw new TimeoutException("Desktop connection did not register at Relay.");
        }
        async Task<HttpResponseMessage> PostKick(string messageId, string body)
        {
            var stamp = DateTimeOffset.UtcNow.ToString("O"); var bytes = Encoding.UTF8.GetBytes(body);
            var signed = Encoding.UTF8.GetBytes($"{messageId}.{stamp}.").Concat(bytes).ToArray();
            var signature = Convert.ToBase64String(rsa.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/webhooks/kick") { Content = new ByteArrayContent(bytes) };
            request.Headers.Add("Kick-Event-Message-Id", messageId); request.Headers.Add("Kick-Event-Message-Timestamp", stamp);
            request.Headers.Add("Kick-Event-Signature", signature); request.Headers.Add("Kick-Event-Type", "channel.followed");
            using var isolated = new HttpClient();
            return await isolated.SendAsync(request);
        }
        try
        {
            relay = StartRelay(); await WaitHealthAsync();
            _output.WriteLine($"Local Relay idle working set after health startup: {relay.WorkingSet64 / 1024.0 / 1024.0:0.0} MiB");
            var enrollResponse = await http.PostAsJsonAsync(baseUrl + "/api/v1/enroll", new { code = enrollment, channels = new[] { "kick:101", "facebook:page-1" } }); enrollResponse.EnsureSuccessStatusCode();
            using var enrolled = JsonDocument.Parse(await enrollResponse.Content.ReadAsStringAsync());
            var installId = enrolled.RootElement.GetProperty("installationId").GetString()!;
            var refresh = enrolled.RootElement.GetProperty("refreshToken").GetString()!;
            var originalAccess = enrolled.RootElement.GetProperty("accessToken").GetString()!;
            using (var wrongChannel = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/v1/kick/subscriptions/reconcile")
            { Content = JsonContent.Create(new { channelId = "202", kickAccessToken = "local-test-token" }) })
            {
                wrongChannel.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", originalAccess);
                using var denied = await http.SendAsync(wrongChannel); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }

            await using var desktop = new WebhookRelayClient(); await using var unified = new UnifiedChatService();
            var connectedCount = 0; var firstConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            desktop.StateChanged += state => { if (state == RelayConnectionState.Connected)
                { var n = Interlocked.Increment(ref connectedCount); if (n == 1) firstConnected.TrySetResult(); if (n >= 2) secondConnected.TrySetResult(); } };
            var received = new ConcurrentDictionary<string, TaskCompletionSource<RelayEvent>>();
            var fbEvent = new TaskCompletionSource<RelayEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            desktop.EventReceived += evt =>
            {
                var kind = evt.Provider.Equals("kick", StringComparison.OrdinalIgnoreCase) ? evt.EventType switch
                { "channel.followed" => StreamEventType.Follow, _ => StreamEventType.Custom } : StreamEventType.Share;
                unified.PublishRelayEvent(new StreamEvent { Id = evt.EventId, Platform = evt.Provider, EventType = kind, ActorName = "fixture" });
                if (evt.Provider == "facebook") fbEvent.TrySetResult(evt);
                if (received.TryGetValue(evt.EventId, out var completion)) completion.TrySetResult(evt);
            };
            var eventCount = 0; unified.EventReceived += _ => Interlocked.Increment(ref eventCount);
            using var desktopProcess = Process.GetCurrentProcess();
            var cpuSample = Stopwatch.StartNew(); var cpuStart = desktopProcess.TotalProcessorTime;
            await Task.Delay(750); var baselineCpuPercent = (desktopProcess.TotalProcessorTime - cpuStart).TotalMilliseconds / cpuSample.Elapsed.TotalMilliseconds * 100;
            desktop.Start(baseUrl, installId, refresh, ["kick:101", "facebook:page-1"]);
            await firstConnected.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitConnectionAsync(1);
            cpuSample.Restart(); cpuStart = desktopProcess.TotalProcessorTime;
            await Task.Delay(750); var connectedCpuPercent = (desktopProcess.TotalProcessorTime - cpuStart).TotalMilliseconds / cpuSample.Elapsed.TotalMilliseconds * 100;
            _output.WriteLine($"Desktop relay-idle CPU sample (test runner host): baseline {baselineCpuPercent:0.00}%, connected {connectedCpuPercent:0.00}%, delta {connectedCpuPercent - baselineCpuPercent:+0.00;-0.00;0.00} pp");
            var initialKick = new TaskCompletionSource<RelayEvent>(TaskCreationOptions.RunContinuationsAsynchronously); received["kick-e2e-1"] = initialKick;
            using (var response = await PostKick("kick-e2e-1", "{\"broadcaster\":{\"user_id\":101},\"follower\":{\"user_id\":41,\"username\":\"viewer1\"},\"created_at\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}"))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await initialKick.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (var duplicate = await PostKick("kick-e2e-1", "{\"broadcaster\":{\"user_id\":101},\"follower\":{\"user_id\":41,\"username\":\"viewer1\"},\"created_at\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}"))
                Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            var noDuplicate = new TaskCompletionSource<RelayEvent>(TaskCreationOptions.RunContinuationsAsynchronously); received["kick-e2e-1"] = noDuplicate;
            await Assert.ThrowsAsync<TimeoutException>(async () => await noDuplicate.Task.WaitAsync(TimeSpan.FromMilliseconds(300)));

            using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/webhooks/kick") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            invalidRequest.Headers.Add("Kick-Event-Message-Id", "bad"); invalidRequest.Headers.Add("Kick-Event-Message-Timestamp", DateTimeOffset.UtcNow.ToString("O"));
            invalidRequest.Headers.Add("Kick-Event-Signature", Convert.ToBase64String(new byte[256])); invalidRequest.Headers.Add("Kick-Event-Type", "channel.followed");
            using var invalidResponse = await http.SendAsync(invalidRequest); Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);

            var fbChallenge = await http.GetAsync(baseUrl + "/webhooks/facebook?hub.mode=subscribe&hub.verify_token=" + Uri.EscapeDataString(fbVerify) + "&hub.challenge=challenge-1");
            Assert.Equal("challenge-1", await fbChallenge.Content.ReadAsStringAsync());
            var fbBody = JsonSerializer.SerializeToUtf8Bytes(new { @object = "page", entry = new[] { new { id = "page-1", time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), changes = new[] { new { field = "feed", value = new { item = "post", verb = "add", post_id = "p1" } } } } } });
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(fbSecret)))
            using (var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/webhooks/facebook") { Content = new ByteArrayContent(fbBody) })
            {
                request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(hmac.ComputeHash(fbBody)).ToLowerInvariant());
                using var response = await http.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            var fbReceived = await fbEvent.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal("facebook", fbReceived.Provider);
            using (var badFacebook = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/webhooks/facebook") { Content = new StringContent("{}") })
            {
                badFacebook.Headers.Add("X-Hub-Signature-256", "sha256=" + new string('0', 64));
                using var response = await http.SendAsync(badFacebook); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            using (var malformed = await PostKick("kick-malformed", "{")) Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            using (var oversized = await PostKick("kick-oversized", new string('x', 70 * 1024))) Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);

            var nextEvent = new TaskCompletionSource<RelayEvent>(TaskCreationOptions.RunContinuationsAsynchronously); received["kick-e2e-2"] = nextEvent;
            var latencyClock = Stopwatch.StartNew();
            using (var response = await PostKick("kick-e2e-2", "{\"broadcaster\":{\"user_id\":101},\"follower\":{\"user_id\":43,\"username\":\"viewer2\"},\"created_at\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}"))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await nextEvent.Task.WaitAsync(TimeSpan.FromSeconds(5)); latencyClock.Stop(); Assert.True(latencyClock.Elapsed < TimeSpan.FromSeconds(2));
            _output.WriteLine($"Local Kick webhook-to-Desktop event latency: {latencyClock.Elapsed.TotalMilliseconds:0.0} ms");
            Assert.True(Volatile.Read(ref eventCount) >= 3);

            relay.Kill(true); await relay.WaitForExitAsync(); await Task.Delay(500);
            Assert.NotEqual(RelayConnectionState.Connected, desktop.State);
            relay = StartRelay(); await WaitHealthAsync(); await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await WaitConnectionAsync(1);
            var postRestart = new TaskCompletionSource<RelayEvent>(TaskCreationOptions.RunContinuationsAsynchronously); received["kick-e2e-3"] = postRestart;
            using (var response = await PostKick("kick-e2e-3", "{\"broadcaster\":{\"user_id\":101},\"follower\":{\"user_id\":44,\"username\":\"after-restart\"},\"created_at\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}"))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await postRestart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var revoke = await http.PostAsJsonAsync(baseUrl + "/api/v1/session/revoke", new { installationId = installId, refreshToken = refresh });
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
            var oldRefresh = await http.PostAsJsonAsync(baseUrl + "/api/v1/session/refresh", new { installationId = installId, refreshToken = refresh });
            Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
            using (var oldSession = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/v1/kick/subscriptions/reconcile")
            { Content = JsonContent.Create(new { channelId = "101", kickAccessToken = "local-test-token" }) })
            {
                oldSession.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", originalAccess);
                using var denied = await http.SendAsync(oldSession); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
            await Task.Delay(250);
            Assert.NotEqual(RelayConnectionState.Connected, desktop.State);
        }
        finally
        {
            if (relay is { HasExited: false }) { relay.Kill(true); await relay.WaitForExitAsync(); }
            try { File.Delete(storage); } catch { }
        }
    }
}
