using AravalsStream.Core.Alerts;
using AravalsStream.Core.Chat;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using Xunit;

namespace AravalsStream.Tests;

public sealed class Phase16Tests
{
    private sealed class Provider(string platform) : IChatProvider
    {
        public string Platform => platform;
        public bool IsConnected { get; set; } = true;
        public string? StatusMessage => null;
        public bool FailSend { get; set; }
        public event Action<ChatMessage>? MessageReceived;
        public event Action<string>? StatusChanged { add { } remove { } }
        public void Emit(ChatMessage message) => MessageReceived?.Invoke(message);
        public Task StartAsync(string liveChatId, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task<bool> SendMessageAsync(string text, CancellationToken ct = default) =>
            FailSend ? Task.FromException<bool>(new InvalidOperationException("send failed")) : Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task UnifiedChat_DeduplicatesByProviderId_OrdersAndIsolatesSendFailure()
    {
        await using var service = new UnifiedChatService();
        var youtube = new Provider("YouTube"); var twitch = new Provider("Twitch") { FailSend = true };
        service.Register(youtube); service.Register(twitch);
        var received = new List<ChatMessage>();
        var bothReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.MessageReceived += message =>
        {
            lock (received)
            {
                received.Add(message);
                if (received.Count == 2) bothReceived.TrySetResult();
            }
        };
        var time = DateTimeOffset.UtcNow;
        Assert.True(service.Publish(new() { Id = "a", Platform = "YouTube", Text = "same", Timestamp = time.AddSeconds(1) }));
        Assert.False(service.Publish(new() { Id = "a", Platform = "YouTube", Text = "same", Timestamp = time }));
        Assert.True(service.Publish(new() { Id = "b", Platform = "YouTube", Text = "same", Timestamp = time }));
        await bothReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (received) Assert.Equal(["b", "a"], received.Select(m => m.Id).ToArray());
        var sends = await service.SendAsync("hello", ["YouTube", "Twitch"]);
        Assert.True(sends[0].Sent); Assert.False(sends[1].Sent);
        Assert.Equal(2, service.History.Count);
    }

    [Fact]
    public void CapabilitiesAndEventMapping_RespectOfficialPlatformLimits()
    {
        Assert.True(ChatProviderCapabilities.For("Kick").RequiresWebhook);
        Assert.False(ChatProviderCapabilities.For("Kick").CanReceive);
        Assert.False(ChatProviderCapabilities.For("TikTok").CanSend);
        Assert.Null(StreamEventMapper.FromChat(new() { Platform = "TikTok", MessageType = ChatMessageType.Gift }));
        Assert.Null(StreamEventMapper.FromChat(new() { Platform = "YouTube", MessageType = ChatMessageType.StandardMessage }));
        var raid = StreamEventMapper.FromChat(new() { Platform = "Twitch", MessageType = ChatMessageType.Raid,
            EventQuantity = 45, AuthorName = "Alex" });
        Assert.Equal(45, raid?.Quantity);
        var paid = StreamEventMapper.FromChat(new() { Platform = "YouTube", MessageType = ChatMessageType.SuperChat,
            SuperChatAmount = "$5", SuperChatComment = "Hi" });
        Assert.Equal("$5", paid?.Metadata["displayAmount"]);
    }

    [Fact]
    public void AlertQueue_PrioritizesBoundsAndGroupsFollowers()
    {
        var settings = new AlertSettings { MaxQueueLength = 2, CooldownMilliseconds = 0 };
        var engine = new AlertEngine(settings);
        var now = DateTimeOffset.UtcNow;
        Assert.True(engine.Enqueue(Event("f1", StreamEventType.Follow), now));
        Assert.True(engine.Enqueue(Event("f2", StreamEventType.Follow), now.AddSeconds(1)));
        Assert.Equal(1, engine.QueuedCount);
        Assert.True(engine.Enqueue(Event("p", StreamEventType.PaidMessage), now.AddSeconds(1)));
        Assert.False(engine.Enqueue(Event("r", StreamEventType.Raid), now.AddSeconds(1)));
        Assert.Equal(StreamEventType.PaidMessage, engine.Tick(now.AddSeconds(2))?.Event.EventType);
    }

    [Fact]
    public void TemplateAndOverlayFilter_HandleUntrustedTextAsPlainText()
    {
        var item = Event("x", StreamEventType.Custom);
        item.ActorName = "<script>alert(1)</script>";
        Assert.Equal("<script>alert(1)</script> ", AlertTemplate.Render("{user} {unknown}", item));
        var settings = new ChatOverlaySettings { Enabled = true, HideLinks = true, BlockedWords = ["spam"] };
        Assert.False(ChatOverlayFilter.ShouldShow(new() { Platform = "YouTube", Text = "http://evil.test" }, settings, DateTimeOffset.UtcNow));
        Assert.False(ChatOverlayFilter.ShouldShow(new() { Platform = "YouTube", Text = "SPAM" }, settings, DateTimeOffset.UtcNow));
        Assert.True(ChatOverlayFilter.ShouldShow(new() { Platform = "YouTube", Text = "hello" }, settings, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AlertRoutingAndAnimation_AreIndependentPerCanvas()
    {
        var definition = new AlertDefinition { ShowHorizontal = true, ShowVertical = false,
            EnterMilliseconds = 300, DisplayMilliseconds = 1000, ExitMilliseconds = 300 };
        var start = DateTimeOffset.UtcNow;
        var instance = new AlertInstance { Event = Event("x", StreamEventType.Follow),
            Definition = definition, StartedAt = start };
        Assert.True(definition.ShowHorizontal); Assert.False(definition.ShowVertical);
        Assert.InRange(instance.OpacityAt(start.AddMilliseconds(150)), .49, .51);
        Assert.Equal(0, instance.OpacityAt(start.AddMilliseconds(1600)));
    }

    [Fact]
    public void AlertSoundRouting_ExcludesRecordingAndUnselectedPlatform()
    {
        var matrix = new AudioRoutingMatrix();
        var channelId = Guid.NewGuid();
        var youtube = new PlatformDestinationGroup { PlatformType = PlatformType.YouTube, Routing = RoutingMode.Horizontal };
        var twitch = new PlatformDestinationGroup { PlatformType = PlatformType.Twitch, Routing = RoutingMode.Horizontal };
        var definition = new AlertDefinition { RecordSound = false, SoundPlatforms = ["YouTube"] };
        AlertSoundRouting.Configure(matrix, channelId, definition, [youtube, twitch]);
        Assert.False(matrix.IsRouteEnabled(channelId, "Recording"));
        Assert.True(matrix.IsRouteEnabled(channelId, youtube.Horizontal.Id.ToString()));
        Assert.False(matrix.IsRouteEnabled(channelId, twitch.Horizontal.Id.ToString()));
    }

    [Fact]
    public void RecordingCanvas_ScalesLogicalScenePositionsToOutputResolution()
    {
        var source = new SceneSource { Type = SourceType.Alerts,
            HorizontalTransform = new SourceTransform { X = 960, Y = 540, Width = 480, Height = 270 } };
        var scene = new Scene(); scene.Sources.Add(source);
        var frame = new RawVideoFrame(1, 1, 4, [0, 0, 255, 255]);
        var output = new byte[1280 * 720 * 4];
        CompositedFrameRenderer.Render(1280, 720, OutputMode.Horizontal, scene, _ => frame,
            output, logicalWidth: 1920, logicalHeight: 1080);
        var inside = (450 * 1280 + 800) * 4;
        var outside = (450 * 1280 + 500) * 4;
        Assert.Equal(255, output[inside + 2]);
        Assert.Equal(0, output[outside + 2]);
    }

    private static StreamEvent Event(string id, StreamEventType type) => new()
    { Id = id, Platform = "Twitch", EventType = type, ActorId = id, ActorName = "Test" };
}
