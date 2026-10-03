using AravalsStream.Core.Platforms;

namespace AravalsStream.Core.Models;

public static class DestinationValidation
{
    public static string? Validate(Destination d, bool hasKey)
    {
        if (string.IsNullOrWhiteSpace(d.Name)) return "Enter a destination name.";
        if (!Uri.TryCreate(d.StreamUrl, UriKind.Absolute, out var url) ||
            (url.Scheme != "rtmp" && url.Scheme != "rtmps") || string.IsNullOrWhiteSpace(url.Host))
            return "Server URL must begin with rtmp:// or rtmps:// and include a host.";
        if (!string.Equals(d.Protocol, url.Scheme, StringComparison.OrdinalIgnoreCase)) return "Protocol must match the server URL scheme.";
        if (!hasKey) return "Enter a stream key.";
        if (d.OutputMode == OutputMode.Both) return "Choose Horizontal or Vertical output.";
        if (d.VideoBitrateKbps < 500 || d.VideoBitrateKbps > 100000) return "Video bitrate must be 500–100000 kbps.";
        if (d.AudioBitrateKbps < 32 || d.AudioBitrateKbps > 320) return "Audio bitrate must be 32–320 kbps.";
        if (d.FrameRate != 30 && d.FrameRate != 60) return "Choose 30 or 60 FPS.";
        if (d.KeyframeIntervalSeconds < 1 || d.KeyframeIntervalSeconds > 10) return "Keyframe interval must be 1–10 seconds.";
        return null;
    }

    public static string? ValidateGroup(PlatformDestinationGroup g, bool hasKey)
    {
        if (string.IsNullOrWhiteSpace(g.Name)) return "Enter a destination name.";
        if (g.Routing == RoutingMode.Off) return null;

        var profile = PlatformRegistry.Get(g.PlatformType);
        if (g.Routing == RoutingMode.Horizontal && !profile.SupportsHorizontal)
            return $"{profile.DisplayName} does not support horizontal streaming.";
        if (g.Routing == RoutingMode.Vertical && !profile.SupportsVertical)
            return $"{profile.DisplayName} does not support vertical streaming.";
        if (g.Routing == RoutingMode.Both && (!profile.SupportsHorizontal || !profile.SupportsVertical))
            return $"{profile.DisplayName} does not support simultaneous Horizontal and Vertical streaming.";
        if (g.ConfigurationMode == ConfigurationMode.NativeApi && g.PlatformType == PlatformType.Facebook)
        {
            if (g.Routing == RoutingMode.Both) return "Native Facebook Page uses one output.";
            if (string.IsNullOrWhiteSpace(g.FacebookPageId)) return "Select an authorized Facebook Page.";
            // Meta creates a new ingest URL at Start. Validate encoder settings with a placeholder only here.
            foreach (var child in g.GetActiveDestinations())
            {
                var draft = child.Copy();
                draft.StreamUrl = "rtmps://example.invalid/rtmp";
                draft.Protocol = "RTMPS";
                var childErr = Validate(draft, hasKey: true);
                if (childErr != null) return $"{child.Name}: {childErr}";
            }
            return null;
        }

        if (!Uri.TryCreate(g.ServerUrl, UriKind.Absolute, out var url) ||
            (url.Scheme != "rtmp" && url.Scheme != "rtmps") || string.IsNullOrWhiteSpace(url.Host))
            return "Server URL must begin with rtmp:// or rtmps:// and include a host.";

        if (!hasKey) return "Enter a stream key.";

        foreach (var child in g.GetActiveDestinations())
        {
            var childErr = Validate(child, hasKey: true);
            if (childErr != null) return $"{child.Name}: {childErr}";
        }

        return null;
    }

    public static List<string> GetWarnings(PlatformDestinationGroup g)
    {
        var warnings = new List<string>();
        var profile = PlatformRegistry.Get(g.PlatformType);
        foreach (var child in g.GetActiveDestinations())
        {
            if (child.VideoBitrateKbps > profile.MaxVideoBitrateKbps)
            {
                warnings.Add($"{child.Name} video bitrate ({child.VideoBitrateKbps} kbps) exceeds recommended limit for {profile.DisplayName} ({profile.MaxVideoBitrateKbps} kbps).");
            }
        }
        return warnings;
    }
}
