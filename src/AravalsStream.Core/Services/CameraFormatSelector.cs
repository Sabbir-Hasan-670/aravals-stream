namespace AravalsStream.Core.Services;

public sealed record VideoFormatChoice(int Width, int Height, int FramesPerSecond, string Subtype);

public static class CameraFormatSelector
{
    public static VideoFormatChoice? Recommended(IEnumerable<VideoFormatChoice> supported)
    {
        var choices = supported.Where(f => f.Width > 0 && f.Height > 0 && f.FramesPerSecond > 0).ToList();
        if (choices.Count == 0) return null;
        var suitable = choices.Where(f => f.Width <= 1280 && f.Height <= 720 && f.FramesPerSecond <= 30).ToList();
        if (suitable.Count == 0)
            suitable = choices.Where(f => f.Width <= 1920 && f.Height <= 1080 && f.FramesPerSecond <= 30).ToList();
        if (suitable.Count == 0) suitable = choices;
        return suitable.OrderByDescending(f => f.Width * f.Height)
            .ThenByDescending(f => f.FramesPerSecond)
            .ThenBy(f => f.Subtype).First();
    }
}
