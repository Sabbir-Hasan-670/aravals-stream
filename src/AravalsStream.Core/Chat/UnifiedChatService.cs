using System.Threading.Channels;
using System.Text.Json;
using AravalsStream.Core.Models;

namespace AravalsStream.Core.Chat;

public sealed record ChatSendResult(string Platform, bool Sent, string? Error);

public sealed class UnifiedChatService : IAsyncDisposable
{
    private readonly Channel<ChatMessage> _queue = Channel.CreateBounded<ChatMessage>(
        new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Dictionary<string, IChatProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Action<ChatMessage>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentKeys = new();
    private readonly HashSet<string> _dedupe = new(StringComparer.Ordinal);
    private readonly Queue<string> _eventDedupeQueue = new();
    private readonly HashSet<string> _eventDedupe = new(StringComparer.Ordinal);
    private readonly Queue<ChatMessage> _history = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Channel<ChatMessage> _logQueue = Channel.CreateBounded<ChatMessage>(
        new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _logWorker;
    private string? _logDirectory;
    public event Action<ChatMessage>? MessageReceived;
    public event Action<StreamEvent>? EventReceived;
    public IReadOnlyList<ChatMessage> History { get { lock (_gate) return _history.ToArray(); } }

    public UnifiedChatService()
    {
        _worker = Task.Run(ProcessAsync);
        _logWorker = Task.Run(WriteLogsAsync);
    }

    public void ConfigureChatLog(bool enabled, string directory)
    {
        _logDirectory = enabled ? directory : null;
    }

    public void Register(IChatProvider provider)
    {
        lock (_gate)
        {
            Unregister(provider.Platform);
            Action<ChatMessage> handler = message => { Publish(message); };
            provider.MessageReceived += handler;
            _providers[provider.Platform] = provider;
            _handlers[provider.Platform] = handler;
        }
    }

    public void Unregister(string platform)
    {
        lock (_gate)
        {
            if (_providers.Remove(platform, out var provider) && _handlers.Remove(platform, out var handler))
                provider.MessageReceived -= handler;
        }
    }

    public bool Publish(ChatMessage message)
    {
        if (!IsValidMessage(message)) return false;
        if (!ChatProviderCapabilities.For(message.Platform).CanReceive) return false;
        return QueueMessage(message);
    }

    public bool PublishVerifiedWebhook(ChatMessage message)
    {
        if (!IsValidMessage(message) || !PlatformEventCapabilities.For(message.Platform).SupportsRealtimeWebhook) return false;
        return QueueMessage(message);
    }

    private static bool IsValidMessage(ChatMessage message) =>
        !string.IsNullOrWhiteSpace(message.Platform) && !string.IsNullOrWhiteSpace(message.Id);

    private bool QueueMessage(ChatMessage message)
    {
        var key = message.Platform.ToLowerInvariant() + ":" + (message.RawProviderMessageId ?? message.Id);
        lock (_gate)
        {
            if (!_dedupe.Add(key)) return false;
            _recentKeys.Enqueue(key);
            while (_recentKeys.Count > 2048) _dedupe.Remove(_recentKeys.Dequeue());
        }
        return _queue.Writer.TryWrite(message);
    }

    public bool PublishRelayEvent(StreamEvent item)
    {
        if (item.Id.Length is 0 or > 200 || item.Platform.Length is 0 or > 40) return false;
        lock (_gate)
        {
            var key = "relay:" + item.Platform.ToLowerInvariant() + ":" + item.Id;
            if (!_eventDedupe.Add(key)) return false;
            _eventDedupeQueue.Enqueue(key);
            while (_eventDedupeQueue.Count > 2048) _eventDedupe.Remove(_eventDedupeQueue.Dequeue());
        }
        try { EventReceived?.Invoke(item); return true; } catch { return false; }
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_stop.Token))
            {
                var batch = new List<ChatMessage>();
                while (_queue.Reader.TryRead(out var next) && batch.Count < 256) batch.Add(next);
                await Task.Delay(150, _stop.Token);
                while (_queue.Reader.TryRead(out var message) && batch.Count < 256) batch.Add(message);
                foreach (var item in batch.OrderBy(m => m.Timestamp))
                {
                    lock (_gate)
                    {
                        _history.Enqueue(item);
                        while (_history.Count > 500) _history.Dequeue();
                    }
                    try { MessageReceived?.Invoke(item); } catch { }
                    if (_logDirectory != null) _logQueue.Writer.TryWrite(item);
                    var streamEvent = StreamEventMapper.FromChat(item);
                    if (streamEvent != null) try { EventReceived?.Invoke(streamEvent); } catch { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteLogsAsync()
    {
        try
        {
            await foreach (var item in _logQueue.Reader.ReadAllAsync(_stop.Token))
            {
                var directory = _logDirectory;
                if (directory == null) continue;
                try
                {
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
                    var safe = new { item.Id, item.Platform, item.ChannelId, item.AuthorId,
                        item.AuthorName, item.Text, item.Timestamp, item.MessageType };
                    await File.AppendAllTextAsync(path, JsonSerializer.Serialize(safe) + Environment.NewLine, _stop.Token);
                }
                catch (Exception) { /* Logging must never interrupt chat delivery. */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    public IReadOnlyList<string> AvailableSendTargets()
    {
        lock (_gate) return _providers.Values.Where(p => p.IsConnected && p.Capabilities.CanSend)
            .Select(p => p.Platform).OrderBy(p => p).ToArray();
    }

    public async Task<IReadOnlyList<ChatSendResult>> SendAsync(string text, IEnumerable<string> selectedTargets,
        CancellationToken ct = default)
    {
        var results = new List<ChatSendResult>();
        foreach (var name in selectedTargets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IChatProvider? provider;
            lock (_gate) _providers.TryGetValue(name, out provider);
            if (provider == null || !provider.Capabilities.CanSend || !provider.IsConnected)
            { results.Add(new(name, false, "Unavailable")); continue; }
            try { results.Add(new(name, await provider.SendMessageAsync(text, ct), null)); }
            catch (Exception ex) { results.Add(new(name, false, ex.GetType().Name)); }
        }
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _queue.Writer.TryComplete();
        _logQueue.Writer.TryComplete();
        await _worker;
        await _logWorker;
        lock (_gate) foreach (var name in _providers.Keys.ToArray()) Unregister(name);
        _stop.Dispose();
    }
}
