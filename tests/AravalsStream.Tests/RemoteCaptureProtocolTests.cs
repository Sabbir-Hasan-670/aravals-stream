using AravalsStream.Core.RemoteCapture;
using Xunit;

namespace AravalsStream.Tests;

public sealed class RemoteCaptureProtocolTests
{
    private sealed class TestSecretStorage : AravalsStream.Core.Interfaces.ISecretStorage
    {
        private readonly Dictionary<string, string> _values = [];
        public int StoreCount { get; private set; }
        public Task StoreAsync(string key, string secret, CancellationToken cancellationToken = default) { StoreCount++; _values[key] = secret; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) { _values.Remove(key); return Task.CompletedTask; }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) { }
    }

    [Fact]
    public async Task RegistryCredentialReadCompletesWhenCallerBlocksOnUiDispatcher()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aravals-registry-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var deviceId = Guid.NewGuid();
        var secrets = new TestSecretStorage();
        var registry = new PairedDeviceRegistry(secrets, Path.Combine(directory, "devices.json"));
        await registry.SaveAsync(new PairedRemoteDevice(deviceId, "test", "127.0.0.1", 45821, 1, "test", "thumbprint", deviceId.ToString("N"), DateTimeOffset.UtcNow), "test-passphrase");
        using var completed = new ManualResetEventSlim();
        string? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try { result = registry.GetPassphraseAsync(deviceId).GetAwaiter().GetResult(); }
            catch (Exception ex) { failure = ex; }
            finally { completed.Set(); }
        }) { IsBackground = true };
        thread.Start();

        try
        {
            Assert.True(completed.Wait(TimeSpan.FromSeconds(5)), "The credential read deadlocked while its caller blocked the dispatcher.");
            Assert.Null(failure);
            Assert.Equal("test-passphrase", result);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void PairingCodeIsSixDigitsAndRejectsMalformedInput()
    {
        var code = RemoteCaptureProtocol.CreatePairingCode();
        Assert.Matches("^\\d{6}$", code);
        Assert.True(RemoteCaptureProtocol.ValidatePairingCode(code));
        Assert.False(RemoteCaptureProtocol.ValidatePairingCode("12345"));
        Assert.False(RemoteCaptureProtocol.ValidatePairingCode("12345x"));
        Assert.False(RemoteCaptureProtocol.ValidatePairingCode(null));
    }

    [Fact]
    public void ProtocolCompatibilityRequiresExactVersion()
    {
        Assert.True(RemoteCaptureProtocol.IsCompatible(1));
        Assert.False(RemoteCaptureProtocol.IsCompatible(2));
    }

    [Fact]
    public void SrtConfigurationBoundsLatencyAndRedactsPassphrase()
    {
        var options = new SrtConnectionOptions("192.168.1.20", 45820, 250, "very-secret-passphrase");
        Assert.Contains("latency=250", options.ToUri().Query);
        Assert.DoesNotContain("very-secret-passphrase", options.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SrtConnectionOptions("host", 45820, 5000, "very-secret-passphrase").ToUri());
        Assert.Throws<ArgumentException>(() => new SrtConnectionOptions("host", 45820, 250, "short").ToUri());
    }

    [Theory]
    [InlineData(RemoteLatencyMode.UltraLow, 80)]
    [InlineData(RemoteLatencyMode.Balanced, 250)]
    [InlineData(RemoteLatencyMode.Stable, 600)]
    public void LatencyModesMapToBoundedSrtLatency(RemoteLatencyMode mode, int expected) =>
        Assert.Equal(expected, RemoteCaptureProtocol.LatencyFor(mode));

    [Fact]
    public void HardwareEncoderSelectionUsesRequiredPreferenceAndFallback()
    {
        Assert.Equal("h264_nvenc", RemoteCaptureProtocol.SelectH264Encoder(["libx264", "h264_nvenc", "h264_qsv"]));
        Assert.Equal("h264_qsv", RemoteCaptureProtocol.SelectH264Encoder(["libx264", "h264_qsv"]));
        Assert.Equal("h264_amf", RemoteCaptureProtocol.SelectH264Encoder(["h264_amf", "libx264"]));
        Assert.Equal("libx264", RemoteCaptureProtocol.SelectH264Encoder(["libx264"]));
        Assert.Equal("libx264", RemoteCaptureProtocol.SelectH264Encoder([]));
    }

    [Fact]
    public void DiscoveryPacketContainsOnlyPublicCapabilityMetadata()
    {
        var packet = RemoteCaptureProtocol.SerializeAdvertisement(new RemoteAgentAdvertisement(
            Guid.NewGuid(), "GAMING-PC", "0.19.4-beta", 1, 45820, ["display"], ["h264_nvenc"]));
        Assert.True(RemoteCaptureProtocol.TryParseAdvertisement(packet, out var parsed));
        Assert.Equal("GAMING-PC", parsed!.ComputerName);
        Assert.DoesNotContain("passphrase", System.Text.Encoding.UTF8.GetString(packet), StringComparison.OrdinalIgnoreCase);
        Assert.False(RemoteCaptureProtocol.TryParseAdvertisement(new byte[5000], out _));
        Assert.False(RemoteCaptureProtocol.TryParseAdvertisement("{}"u8, out _));
    }

    [Fact]
    public void RemoteStateMachineBacksOffAndResetsAfterRecovery()
    {
        var machine = new RemoteSourceStateMachine();
        var now = DateTimeOffset.UtcNow;
        machine.Transition(RemoteSourceState.Discovered, now);
        machine.Transition(RemoteSourceState.Connecting, now);
        machine.Transition(RemoteSourceState.Reconnecting, now);
        Assert.False(machine.IsRetryDue(now));
        Assert.True(machine.IsRetryDue(now.AddSeconds(1)));
        Assert.Equal(1, machine.ReconnectCount);
        machine.Transition(RemoteSourceState.Connecting, now.AddSeconds(1));
        machine.Transition(RemoteSourceState.Live, now.AddSeconds(2));
        machine.Transition(RemoteSourceState.Reconnecting, now.AddSeconds(3));
        Assert.Equal(now.AddSeconds(4), machine.RetryAt);
    }

    [Fact]
    public void LatestFrameSlotDisposesReplacedVideoInsteadOfGrowingAQueue()
    {
        var disposed = 0;
        var slot = new LatestFrameSlot<TestFrame>(_ => disposed++);
        var older = new TestFrame();
        var newest = new TestFrame();
        slot.Publish(older);
        slot.Publish(newest);
        Assert.Equal(1, disposed);
        Assert.Equal(1, slot.ReplacedCount);
        Assert.Same(newest, slot.Take());
        Assert.Null(slot.Take());
    }

    [Fact]
    public void Nv12ConverterProducesCompositorBgraPixels()
    {
        byte[] nv12 = [16, 235, 16, 235, 128, 128];
        byte[] bgra = new byte[16];

        Nv12BgraConverter.Convert(nv12, bgra, 2, 2);

        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255 }, bgra);
    }

    [Fact]
    public void Bgr24ConverterExpandsPixelsAndSetsOpaqueAlpha()
    {
        byte[] bgr24 = [1, 2, 3, 10, 20, 30];
        byte[] bgra = new byte[8];

        Bgr24BgraConverter.Convert(bgr24, bgra, 2, 1);

        Assert.Equal(new byte[] { 1, 2, 3, 255, 10, 20, 30, 255 }, bgra);
    }

    [Fact]
    public async Task PairedRegistryKeepsPassphraseOutsideMetadataAndForgetsIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AravalsRemoteTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "devices.json");
        var storage = new TestSecretStorage();
        var registry = new PairedDeviceRegistry(storage, path);
        var id = Guid.NewGuid();
        var device = new PairedRemoteDevice(id, "GAMING-PC", "127.0.0.1", 45821, 1,
            "0.19.0-beta", "AABBCC", id.ToString("N"), DateTimeOffset.UtcNow);
        await registry.SaveAsync(device, "secret-phrase-not-in-json");
        Assert.Equal("secret-phrase-not-in-json", await registry.GetPassphraseAsync(id));
        Assert.DoesNotContain("secret-phrase-not-in-json", await File.ReadAllTextAsync(path));
        var remoteRevoked = await registry.ForgetAsync(id);
        Assert.False(remoteRevoked);
        Assert.Null(await registry.GetPassphraseAsync(id));
        Assert.False(File.Exists(path));
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task PairedRegistryRefreshesAgentVersionWithoutRewritingSecret()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AravalsRemoteTests", Guid.NewGuid().ToString("N"));
        var storage = new TestSecretStorage();
        var registry = new PairedDeviceRegistry(storage, Path.Combine(folder, "devices.json"));
        var id = Guid.NewGuid();
        var saved = new PairedRemoteDevice(id, "OLD-PC-NAME", "127.0.0.1", 45821, 1,
            "0.19.1-beta", "AABBCC", id.ToString("N"), DateTimeOffset.UtcNow);
        await registry.SaveAsync(saved, "preserved-secret");
        var writesBeforeRefresh = storage.StoreCount;
        var current = new RemoteAgentAdvertisement(id, "NEW-PC-NAME", "0.19.5-beta", 1, 45821,
            ["display:1920x1080:60"], ["h264_nvenc"], CertificateThumbprint: "AABBCC", State: "Ready");

        var refreshed = await registry.RefreshMetadataAsync(id, "192.168.1.102", current);

        Assert.Equal("0.19.5-beta", refreshed.AgentVersion);
        Assert.Equal("NEW-PC-NAME", refreshed.DisplayName);
        Assert.Equal("preserved-secret", await registry.GetPassphraseAsync(id));
        Assert.Equal(saved.SecretReference, refreshed.SecretReference);
        Assert.Equal(writesBeforeRefresh, storage.StoreCount);
        await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(() => registry.RefreshMetadataAsync(id,
            "192.168.1.102", current with { CertificateThumbprint = "DIFFERENT" }));
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task LifecycleStartsControlBeforeMediaAndStopKeepsControl()
    {
        var controlStarts = 0;
        var mediaStarts = 0;
        var control = new TestDisposable();
        var media = new TestDisposable();
        var runtime = new AgentLifecycleCoordinator(
            () => { controlStarts++; return Task.FromResult<IAsyncDisposable>(control); },
            () => { mediaStarts++; return Task.FromResult<IAsyncDisposable>(media); });

        await runtime.StartAsync();
        Assert.True(runtime.ControlPlaneRunning);
        Assert.False(runtime.MediaPlaneRunning);
        await runtime.StartMediaAsync();
        await runtime.StartMediaAsync();
        Assert.Equal(1, mediaStarts);
        await runtime.StopMediaAsync();
        Assert.True(runtime.ControlPlaneRunning);
        Assert.False(runtime.MediaPlaneRunning);
        Assert.Equal(0, control.DisposeCount);
        Assert.Equal(1, media.DisposeCount);
        await runtime.StartMediaAsync();
        await runtime.DisposeAsync();
        Assert.Equal(1, controlStarts);
        Assert.Equal(2, mediaStarts);
        Assert.Equal(1, control.DisposeCount);
        Assert.Equal(2, media.DisposeCount);
    }

    [Fact]
    public async Task LifecycleKeepsControlRunningWhenMediaStartupFailsAndDisposeIsIdempotent()
    {
        var control = new TestDisposable();
        var runtime = new AgentLifecycleCoordinator(
            () => Task.FromResult<IAsyncDisposable>(control),
            () => throw new InvalidOperationException("capture startup failed"));
        await runtime.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartMediaAsync());
        Assert.True(runtime.ControlPlaneRunning);
        Assert.False(runtime.MediaPlaneRunning);
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        Assert.Equal(1, control.DisposeCount);
    }

    [Fact]
    public async Task LifecycleDisposesControlEvenWhenMediaCleanupFails()
    {
        var control = new TestDisposable();
        var media = new TestDisposable { ThrowOnDispose = true };
        var runtime = new AgentLifecycleCoordinator(
            () => Task.FromResult<IAsyncDisposable>(control),
            () => Task.FromResult<IAsyncDisposable>(media));
        await runtime.StartAsync();
        await runtime.StartMediaAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.DisposeAsync().AsTask());
        Assert.Equal(1, media.DisposeCount);
        Assert.Equal(1, control.DisposeCount);
        await runtime.DisposeAsync();
    }

    [Fact]
    public void AdvertisementCarriesReadyOrStreamingStateWithoutChangingProtocolVersion()
    {
        var ready = new RemoteAgentAdvertisement(Guid.NewGuid(), "AGENT", "0.19.5-beta", 1,
            45821, ["display"], ["libx264"], State: "Ready");
        var streaming = ready with { State = "Streaming" };
        Assert.True(RemoteCaptureProtocol.TryParseAdvertisement(RemoteCaptureProtocol.SerializeAdvertisement(ready), out var parsedReady));
        Assert.True(RemoteCaptureProtocol.TryParseAdvertisement(RemoteCaptureProtocol.SerializeAdvertisement(streaming), out var parsedStreaming));
        Assert.Equal("Ready", parsedReady!.State);
        Assert.Equal("Streaming", parsedStreaming!.State);
        Assert.Equal(1, parsedStreaming.ProtocolVersion);
    }

    private sealed class TestDisposable : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public bool ThrowOnDispose { get; init; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ThrowOnDispose ? ValueTask.FromException(new InvalidOperationException("cleanup failed")) : ValueTask.CompletedTask;
        }
    }

    private sealed class TestFrame { }
}
