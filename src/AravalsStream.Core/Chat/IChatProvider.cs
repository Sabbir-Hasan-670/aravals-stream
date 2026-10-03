using AravalsStream.Core.Models;

namespace AravalsStream.Core.Chat;

public interface IChatProvider : IAsyncDisposable
{
    string Platform { get; }
    bool IsConnected { get; }
    string? StatusMessage { get; }
    ChatProviderCapabilities Capabilities => ChatProviderCapabilities.For(Platform);

    event Action<ChatMessage>? MessageReceived;
    event Action<string>? StatusChanged;

    Task StartAsync(string liveChatId, CancellationToken ct = default);
    Task StopAsync();
    Task<bool> SendMessageAsync(string text, CancellationToken ct = default);
}
