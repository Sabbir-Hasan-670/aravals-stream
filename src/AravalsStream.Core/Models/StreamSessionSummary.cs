namespace AravalsStream.Core.Models;

public static class StreamSessionSummary
{
    public static string GlobalStatus(IEnumerable<DestinationStatus> statuses)
    {
        var all = statuses.ToArray();
        if (all.Any(s => s is DestinationStatus.Live or DestinationStatus.FallingBehind or DestinationStatus.Reconnecting)) return "LIVE";
        if (all.Any(s => s is DestinationStatus.Connecting or DestinationStatus.Restarting)) return "CONNECTING";
        if (all.Any(s => s == DestinationStatus.Stopping)) return "STOPPING";
        if (all.Any(s => s == DestinationStatus.Error)) return "ERROR";
        return "OFFLINE";
    }

    public static int EstimatedUploadKbps(IEnumerable<Destination> destinations) =>
        destinations.Where(d => d.Enabled).Sum(d => d.VideoBitrateKbps + d.AudioBitrateKbps);

    public static int EstimatedUploadKbps(IEnumerable<PlatformDestinationGroup> groups) =>
        groups.Where(g => g.Enabled && g.Routing != RoutingMode.Off)
              .SelectMany(g => g.GetActiveDestinations())
              .Sum(d => d.VideoBitrateKbps + d.AudioBitrateKbps);

    public static double EstimatedUploadMbps(IEnumerable<PlatformDestinationGroup> groups) =>
        EstimatedUploadKbps(groups) / 1000.0;
}
