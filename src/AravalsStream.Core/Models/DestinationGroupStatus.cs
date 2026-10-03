namespace AravalsStream.Core.Models;

public enum DestinationGroupStatus
{
    Disabled,
    Offline,
    Connecting,
    Live,
    Partial,
    Reconnecting,
    Error,
    Stopping
}
