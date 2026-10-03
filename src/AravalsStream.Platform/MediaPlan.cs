using System.Globalization;

namespace AravalsStream.Platform;

public sealed record CanvasSize(int Width, int Height)
{
    public static CanvasSize Horizontal => new(1920, 1080);
    public static CanvasSize Vertical => new(1080, 1920);
}
public sealed record MediaPlan(DesktopPlatform Platform, CaptureInput Video, IReadOnlyList<CaptureInput> Audio,
    CanvasSize Canvas, int FrameRate = 30, int VideoBitrateKbps = 4000, int AudioBitrateKbps = 160);

public static class MediaArguments
{
    public static IReadOnlyList<string> Preview(MediaPlan plan)
    {
        Validate(plan);
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        args.AddRange(PlatformCapture.Arguments(plan.Platform, plan.Video, plan.FrameRate));
        args.AddRange(["-map", "0:v:0", "-an", "-vf", "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2,fps=10", "-c:v", "mjpeg", "-q:v", "5", "-f", "image2pipe", "pipe:1"]);
        return args;
    }

    public static IReadOnlyList<string> Output(MediaPlan plan, string destination, bool recording)
    {
        Validate(plan);
        if (recording) destination = Path.GetFullPath(destination);
        else if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri) || uri.Scheme is not ("rtmp" or "rtmps") || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("A valid RTMP or RTMPS output URL without URL credentials is required.");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        args.AddRange(PlatformCapture.Arguments(plan.Platform, plan.Video, plan.FrameRate));
        foreach (var audio in plan.Audio) args.AddRange(PlatformCapture.Arguments(plan.Platform, audio, plan.FrameRate));
        var filters = new List<string>();
        if (plan.Audio.Count > 0)
        {
            for (var i = 0; i < plan.Audio.Count; i++)
            {
                var input = plan.Audio[i];
                var gain = input.Muted ? "0" : input.Gain.ToString(CultureInfo.InvariantCulture);
                filters.Add($"[{i + 1}:a:0]aresample=48000,volume={gain}[a{i}]");
            }
            filters.Add(string.Concat(Enumerable.Range(0, plan.Audio.Count).Select(i => $"[a{i}]")) +
                $"amix=inputs={plan.Audio.Count}:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.95[audio]");
            args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "0:v:0", "-map", "[audio]"]);
        }
        else args.AddRange(["-map", "0:v:0", "-an"]);
        args.AddRange(["-vf", $"scale={plan.Canvas.Width}:{plan.Canvas.Height}:force_original_aspect_ratio=decrease,pad={plan.Canvas.Width}:{plan.Canvas.Height}:(ow-iw)/2:(oh-ih)/2,format=yuv420p",
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-b:v", plan.VideoBitrateKbps + "k",
            "-maxrate", plan.VideoBitrateKbps + "k", "-bufsize", (plan.VideoBitrateKbps * 2) + "k", "-g", (plan.FrameRate * 2).ToString(CultureInfo.InvariantCulture)]);
        if (plan.Audio.Count > 0) args.AddRange(["-c:a", "aac", "-b:a", plan.AudioBitrateKbps + "k", "-ar", "48000", "-ac", "2"]);
        args.AddRange(["-f", recording ? "matroska" : "flv", destination]);
        return args;
    }

    private static void Validate(MediaPlan plan)
    {
        if (plan.Canvas.Width is < 16 or > 7680 || plan.Canvas.Height is < 16 or > 7680 || plan.Canvas.Width % 2 != 0 || plan.Canvas.Height % 2 != 0)
            throw new ArgumentException("Canvas dimensions must be even and between 16 and 7680 pixels.");
        if (plan.VideoBitrateKbps is < 100 or > 100000 || plan.AudioBitrateKbps is < 32 or > 512) throw new ArgumentException("Invalid encoding bitrate.");
        if (plan.Video.Kind is CaptureKind.Microphone or CaptureKind.DesktopAudio or CaptureKind.TestAudio) throw new ArgumentException("A video source is required.");
        if (plan.Audio.Any(a => a.Kind is not (CaptureKind.Microphone or CaptureKind.DesktopAudio or CaptureKind.TestAudio))) throw new ArgumentException("Audio inputs must be audio sources.");
    }
}
