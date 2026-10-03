using System.Text.Json;

namespace AravalsStream.Core.Models;

public sealed record RelayEvent(string EventId, string Provider, string EventType, DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt, string ChannelId, string InstallationId, int SchemaVersion, JsonElement Payload);
