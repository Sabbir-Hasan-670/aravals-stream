using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AravalsStream.Core.Settings;
using AravalsStream.Relay;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AravalsStream.Tests;

public sealed class Phase20BSubscriptionTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "aravals-phase20b-" + Guid.NewGuid().ToString("N"));
        public readonly IConfiguration Config;
        public readonly RelayState State;
        public readonly Provider Handler = new();
        public readonly HttpClient Http;
        public readonly KickSubscriptionService Service;
        public readonly (string InstallationId, string RefreshToken, string AccessToken) A, B;
        public Fixture()
        {
            Directory.CreateDirectory(Root); using var rsa = RSA.Create(2048);
            Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RELAY_TOKEN_SIGNING_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["RELAY_STORAGE_PATH"] = Path.Combine(Root, "state.json"),
                ["RELAY_ALLOWED_HOSTS"] = "relay.aravals.test",
                ["RELAY_PUBLIC_BASE_URL"] = "https://relay.aravals.test",
                ["KICK_APP_ID"] = "app-fixture", ["KICK_SUBSCRIPTIONS_ENABLED"] = "true",
                ["KICK_CONFIGURED_WEBHOOK_URL"] = "https://relay.aravals.test/webhooks/kick",
                ["KICK_WEBHOOK_PUBLIC_KEY_PEM"] = rsa.ExportSubjectPublicKeyInfoPem(),
                ["RELAY_ENROLLMENT_CREDENTIALS_JSON"] = JsonSerializer.Serialize(new[] {
                    new { code = "enrollment-a-01234567890123456789", channels = new[] { "kick:101" } },
                    new { code = "enrollment-b-01234567890123456789", channels = new[] { "kick:202" } },
                    new { code = "enrollment-fresh-a-01234567890123456789", channels = new[] { "kick:101" } } })
            }).Build();
            State = new(Config); A = State.Enroll("enrollment-a-01234567890123456789")!.Value;
            B = State.Enroll("enrollment-b-01234567890123456789")!.Value;
            Http = new(Handler); Service = new(State, Config, Http, (_, _) => Task.CompletedTask);
        }
        public Task<KickRelaySubscriptionStatus> Check() => Service.ReconcileAsync(A.AccessToken, "101", "kick-access-secret");
        public void Dispose() { Http.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class Provider : HttpMessageHandler
    {
        public readonly List<KickRelaySubscription> Items = [];
        public int Creates, Lists, Deletes, Calls;
        public string Failure = "";
        public bool FailCreate;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var path = request.RequestUri!.AbsolutePath;
            if (Failure == "auth") return new(HttpStatusCode.Unauthorized);
            if (path == "/oauth/token/introspect")
                return Json(new { data = new { active = true, client_id = "app-fixture", token_type = "user", scope = Failure == "scope" ? "user:read" : "user:read events:subscribe" } });
            if (path == "/public/v1/users") return Json(new { data = new[] { new { user_id = Failure == "wrong-user" ? 202 : 101 } } });
            Assert.Equal("/public/v1/events/subscriptions", path);
            if (request.Method == HttpMethod.Get)
            {
                Lists++;
                if (Failure == "timeout") throw new TaskCanceledException();
                if (Failure == "5xx") return new(HttpStatusCode.ServiceUnavailable);
                if (Failure == "malformed") return new(HttpStatusCode.OK) { Content = new StringContent("{kick-access-secret") };
                return Json(new { data = Items.Select(s => new { id = s.Id, @event = s.Event, version = s.Version, app_id = s.AppId, method = "webhook", broadcaster_user_id = 101 }) });
            }
            if (request.Method == HttpMethod.Delete)
            {
                Deletes++; var ids = request.RequestUri.Query.TrimStart('?').Split('&').Select(x => Uri.UnescapeDataString(x.Split('=')[1])).ToArray();
                Items.RemoveAll(s => ids.Contains(s.Id)); return new(HttpStatusCode.NoContent);
            }
            Creates++;
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("webhook", doc.RootElement.GetProperty("method").GetString());
            Assert.False(doc.RootElement.TryGetProperty("callback", out _)); Assert.False(doc.RootElement.TryGetProperty("secret", out _));
            var names = doc.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToArray();
            foreach (var name in names) Items.Add(new(Guid.NewGuid().ToString("N"), name, 1, "app-fixture"));
            if (FailCreate) throw new TaskCanceledException(); // provider created them but response was lost
            if (Failure == "malformed-create") return new(HttpStatusCode.OK) { Content = new StringContent("{kick-access-secret") };
            return Json(new { data = Items.Where(s => names.Contains(s.Event)).Select(s => new { name = s.Event, version = s.Version, subscription_id = s.Id }) });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task CreationTransitionsToActiveOnlyAfterProviderDiscovery()
    {
        using var f = new Fixture(); var result = await f.Check();
        Assert.Equal(RelaySubscriptionState.Active, result.State); Assert.Equal(5, result.Subscriptions.Length);
        Assert.NotNull(result.LastCheckedUtc); Assert.Equal(1, f.Handler.Creates); Assert.Equal(2, f.Handler.Lists);
        Assert.Equal("https://relay.aravals.test/webhooks/kick", result.CallbackUrl);
        var saved = File.ReadAllText(f.Config["RELAY_STORAGE_PATH"]!);
        Assert.DoesNotContain("kick-access-secret", saved); Assert.DoesNotContain(f.A.RefreshToken, saved);
    }

    [Fact]
    public async Task ExistingSubscriptionsAndConcurrentReconnectsDoNotCreateDuplicates()
    {
        using var f = new Fixture();
        foreach (var name in KickSubscriptionService.EventNames) f.Handler.Items.Add(new(name + "-id", name, 1, "app-fixture"));
        var checks = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => f.Check()));
        Assert.All(checks, result => Assert.Equal(RelaySubscriptionState.Active, result.State)); Assert.Equal(0, f.Handler.Creates);
    }

    [Fact]
    public async Task RestartPersistsMetadataAndReconcilesWithoutRecreation()
    {
        using var f = new Fixture(); var first = await f.Check(); var restarted = new RelayState(f.Config);
        Assert.Equal(first, restarted.GetKickSubscription(f.A.AccessToken, "101")! with { Subscriptions = first.Subscriptions, UncertainEvents = first.UncertainEvents });
        var service = new KickSubscriptionService(restarted, f.Config, f.Http);
        Assert.Equal(RelaySubscriptionState.Active, (await service.ReconcileAsync(f.A.AccessToken, "101", "kick-access-secret")).State);
        Assert.Equal(1, f.Handler.Creates);
    }

    [Fact]
    public async Task MissingProviderSubscriptionIsRecreatedWithoutRenewalTimer()
    {
        using var f = new Fixture(); await f.Check(); f.Handler.Items.RemoveAt(0); var result = await f.Check();
        Assert.Equal(RelaySubscriptionState.Active, result.State); Assert.Equal(2, f.Handler.Creates); Assert.Equal(5, f.Handler.Items.Count);
    }

    [Fact]
    public async Task AmbiguousCreateResponseIsDiscoveredInsteadOfRepeated()
    {
        using var f = new Fixture(); f.Handler.FailCreate = true;
        Assert.Equal(RelaySubscriptionState.Error, (await f.Check()).State);
        var restarted = new KickSubscriptionService(new RelayState(f.Config), f.Config, f.Http);
        Assert.Equal(RelaySubscriptionState.Active, (await restarted.ReconcileAsync(f.A.AccessToken, "101", "kick-access-secret")).State);
        Assert.Equal(1, f.Handler.Creates);
    }

    [Fact]
    public async Task UnknownCreationDoesNotSendAnotherPostWhenDiscoveryCannotFindIt()
    {
        using var f = new Fixture(); f.Handler.FailCreate = true; await f.Check(); f.Handler.Items.Clear();
        Assert.Equal(RelaySubscriptionState.Pending, (await f.Check()).State); Assert.Equal(1, f.Handler.Creates);
    }

    [Theory]
    [InlineData("5xx", 3)] [InlineData("timeout", 3)] [InlineData("malformed", 1)]
    public async Task ProviderFailuresAreBoundedRedactedAndNeverActive(string failure, int listCount)
    {
        using var f = new Fixture(); f.Handler.Failure = failure; var result = await f.Check();
        Assert.Equal(RelaySubscriptionState.Error, result.State); Assert.Equal(listCount, f.Handler.Lists); Assert.Equal(0, f.Handler.Creates);
        Assert.DoesNotContain("kick-access-secret", result.Error); Assert.Null(result.LastCheckedUtc);
    }

    [Fact]
    public async Task RevokedOAuthRequiresReauthorization()
    {
        using var f = new Fixture(); f.Handler.Failure = "auth";
        Assert.Equal(RelaySubscriptionState.ReauthorizationRequired, (await f.Check()).State); Assert.Equal(1, f.Handler.Calls);
    }

    [Fact]
    public async Task OldOAuthGrantWithoutSubscriptionScopeRequiresReauthorization()
    {
        using var f = new Fixture(); f.Handler.Failure = "scope";
        Assert.Equal(RelaySubscriptionState.ReauthorizationRequired, (await f.Check()).State); Assert.Equal(0, f.Handler.Creates);
    }

    [Fact]
    public async Task KickTokenForAnotherUserCannotCreateSubscriptions()
    {
        using var f = new Fixture(); f.Handler.Failure = "wrong-user";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Check()); Assert.Equal(0, f.Handler.Creates);
        Assert.Equal(RelaySubscriptionState.ReauthorizationRequired, f.State.GetKickSubscription(f.A.AccessToken, "101")!.State);
    }

    [Fact]
    public async Task MalformedCreateResponsePreservesIntentAndDoesNotReportActive()
    {
        using var f = new Fixture(); f.Handler.Failure = "malformed-create";
        Assert.Equal(RelaySubscriptionState.Error, (await f.Check()).State); Assert.Equal(1, f.Handler.Creates);
        Assert.Equal(5, f.State.GetKickSubscription(f.A.AccessToken, "101")!.UncertainEvents.Length);
        f.Handler.Failure = ""; Assert.Equal(RelaySubscriptionState.Active, (await f.Check()).State); Assert.Equal(1, f.Handler.Creates);
    }

    [Fact]
    public async Task ExplicitDeleteClearsAnUnknownCreateOutcomeWithoutBlindAutomaticRetry()
    {
        using var f = new Fixture(); f.Handler.FailCreate = true; await f.Check(); f.Handler.Items.Clear();
        Assert.Equal(RelaySubscriptionState.Pending, (await f.Check()).State);
        var removed = await f.Service.ReconcileAsync(f.A.AccessToken, "101", "kick-access-secret", true);
        Assert.Empty(removed.UncertainEvents); f.Handler.FailCreate = false;
        Assert.Equal(RelaySubscriptionState.Active, (await f.Check()).State); Assert.Equal(2, f.Handler.Creates);
    }

    [Fact]
    public async Task CrossInstallationCannotManipulateAnotherChannelsSubscriptions()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReconcileAsync(f.B.AccessToken, "101", "kick-access-secret"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReconcileAsync(f.A.AccessToken, "202", "kick-access-secret", true));
        Assert.Equal(0, f.Handler.Calls);
    }

    [Fact]
    public async Task RevokedRelaySessionCannotMutateAndFreshEnrollmentWorks()
    {
        using var f = new Fixture(); await f.Check(); Assert.True(f.State.Revoke(f.A.InstallationId, f.A.RefreshToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Check());
        var fresh = f.State.Enroll("enrollment-fresh-a-01234567890123456789")!.Value;
        Assert.Equal(RelaySubscriptionState.Active, (await f.Service.ReconcileAsync(fresh.AccessToken, "101", "kick-access-secret")).State);
        Assert.Equal(1, f.Handler.Creates);
    }

    [Fact]
    public async Task DeletionRemovesOnlyDiscoveredManagedSubscriptions()
    {
        using var f = new Fixture(); await f.Check(); f.Handler.Items.Add(new("unmanaged", "other.event", 1, "app-fixture"));
        var result = await f.Service.ReconcileAsync(f.A.AccessToken, "101", "kick-access-secret", true);
        Assert.Equal(RelaySubscriptionState.NotConfigured, result.State); Assert.Equal(1, f.Handler.Deletes);
        Assert.Single(f.Handler.Items); Assert.Equal("unmanaged", f.Handler.Items[0].Id);
    }

    [Fact]
    public void SchemaOneMigratesWithoutLosingEnrollmentOrSession()
    {
        using var f = new Fixture(); var path = f.Config["RELAY_STORAGE_PATH"]!;
        var old = JsonNode.Parse(File.ReadAllText(path))!; old["SchemaVersion"] = 1; old.AsObject().Remove("KickSubscriptions"); File.WriteAllText(path, old.ToJsonString());
        var migrated = new RelayState(f.Config);
        Assert.NotNull(migrated.Refresh(f.A.InstallationId, f.A.RefreshToken)); Assert.True(migrated.TryOwnChannel(f.A.InstallationId, "kick", "101"));
        Assert.Null(migrated.Enroll("enrollment-a-01234567890123456789"));
        migrated.SaveKickSubscription(f.A.AccessToken, new("101", RelaySubscriptionState.NotConfigured, null, null, "", [], []));
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(path))!["SchemaVersion"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("http://relay.aravals.test")] [InlineData("https://localhost")] [InlineData("https://127.0.0.1")]
    [InlineData("https://192.168.1.2")] [InlineData("bad-uri")] [InlineData("https://relay.example.invalid")]
    [InlineData("https://user:password@relay.aravals.test")] [InlineData("https://relay.aravals.test?token=secret")]
    public void PublicSubscriptionUrlRejectsInsecureAndLocalValues(string url) => Assert.False(RelayPreflight.PublicHttpsUrl(url, out _));

    [Fact]
    public async Task MissingPublicUrlIsNotConfiguredAndDoesNotCallProvider()
    {
        using var f = new Fixture(); f.Config["RELAY_PUBLIC_BASE_URL"] = "http://localhost:5080";
        var result = await f.Check(); Assert.Equal(RelaySubscriptionState.NotConfigured, result.State);
        Assert.Equal("Public Relay URL required for provider subscription.", result.Error); Assert.Equal(0, f.Handler.Calls);
    }

    [Fact]
    public void ProductionPreflightChecksSecretsProviderConfigAndPersistence()
    {
        using var f = new Fixture(); Assert.Empty(RelayPreflight.Validate(f.Config, false));
        f.Config["RELAY_TOKEN_SIGNING_KEY"] = Convert.ToBase64String(new byte[32]);
        f.Config["RELAY_PUBLIC_BASE_URL"] = "http://localhost"; f.Config["KICK_APP_ID"] = "";
        f.Config["FACEBOOK_WEBHOOKS_ENABLED"] = "true"; f.Config["RELAY_STORAGE_PATH"] = f.Root;
        var errors = RelayPreflight.Validate(f.Config, false);
        Assert.Contains(errors, x => x.Contains("SIGNING_KEY")); Assert.Contains(errors, x => x.Contains("HTTPS"));
        Assert.Contains(errors, x => x.Contains("KICK_APP_ID")); Assert.Contains(errors, x => x.Contains("Facebook")); Assert.Contains(errors, x => x.Contains("persistence"));
        Assert.NotNull(RelayPreflight.SigningKeyError("changeme")); Assert.NotNull(RelayPreflight.SigningKeyError(null));
        Assert.NotNull(RelayPreflight.SigningKeyError(Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray())));
    }

    [Fact]
    public void DockerSourcesArePresentAndRuntimeRunsAsNonRootWithoutEmbeddedSecrets()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AravalsStream.sln"))) directory = directory.Parent;
        Assert.NotNull(directory); var repo = directory!.FullName;
        foreach (var project in new[] { "src/AravalsStream.Relay/AravalsStream.Relay.csproj", "src/AravalsStream.Core/AravalsStream.Core.csproj", "Directory.Build.props" }) Assert.True(File.Exists(Path.Combine(repo, project)));
        foreach (var file in new[] { "Dockerfile", "Dockerfile.published" })
        {
            var docker = File.ReadAllText(Path.Combine(repo, "deploy/relay", file));
            Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:8.0", docker); Assert.Contains("USER $APP_UID", docker);
            Assert.Contains("ASPNETCORE_ENVIRONMENT=Production", docker); Assert.Contains("ENTRYPOINT", docker); Assert.DoesNotContain("RELAY_TOKEN_SIGNING_KEY=", docker);
        }
        var ignore = File.ReadAllText(Path.Combine(repo, ".dockerignore"));
        foreach (var entry in new[] { ".env", "**/secrets", "scratch", "**/*.pfx", "**/settings.json", "artifacts" }) Assert.Contains(entry, ignore);
        using var template = JsonDocument.Parse(File.ReadAllLines(Path.Combine(repo, "deploy/relay/.env.example")).Single(x => x.StartsWith("RELAY_ENROLLMENT_CREDENTIALS_JSON=")).Split('=', 2)[1]);
        Assert.Equal(JsonValueKind.Array, template.RootElement.ValueKind);
    }
}
