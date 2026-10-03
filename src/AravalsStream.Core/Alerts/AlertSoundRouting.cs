using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;

namespace AravalsStream.Core.Alerts;

public static class AlertSoundRouting
{
    public static void Configure(AudioRoutingMatrix matrix, Guid channelId, AlertDefinition definition,
        IEnumerable<PlatformDestinationGroup> destinations)
    {
        matrix.SetRoute(channelId, "Recording", definition.RecordSound);
        foreach (var group in destinations)
        {
            var enabled = definition.SoundPlatforms.Contains(group.PlatformType.ToString(), StringComparer.OrdinalIgnoreCase);
            foreach (var target in group.GetActiveDestinations())
                matrix.SetRoute(channelId, target.Id.ToString(), enabled);
        }
    }
}
