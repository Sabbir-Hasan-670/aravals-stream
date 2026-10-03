using AravalsStream.Core.Models;

namespace AravalsStream.Core.Services;

public static class CaptureDemand
{
    public static HashSet<string> ActiveResourceKeys(Scene? activeScene, Func<SceneSource, string?> keyFor)
    {
        if (activeScene is null) return [];
        return activeScene.Sources
            .Where(source => source.Visible &&
                source.Type is (SourceType.DisplayCapture or SourceType.WindowCapture or SourceType.Camera or SourceType.CaptureDevice or SourceType.RemotePc))
            .Select(keyFor).Where(key => key is not null).Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
