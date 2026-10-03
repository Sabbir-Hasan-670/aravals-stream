using System.Globalization;
using AravalsStream.Core.Models;

namespace AravalsStream.Platform;

public sealed record CanvasSize(int Width, int Height)
{
    public static CanvasSize Horizontal => new(1920, 1080);
    public static CanvasSize Vertical => new(1080, 1920);
}
public sealed record MediaPlan(DesktopPlatform Platform, CaptureInput Video, IReadOnlyList<CaptureInput> Audio,
    CanvasSize Canvas, int FrameRate = 30, int VideoBitrateKbps = 4000, int AudioBitrateKbps = 160)
{
    public IReadOnlyList<VideoLayer> Layers { get; init; } = [];
    public bool RenderScene { get; init; }
}
public sealed record VideoLayer(CaptureInput Input, SourceTransform Transform, bool Visible = true);

public static class MediaArguments
{
    public static IReadOnlyList<string> Preview(MediaPlan plan)
    {
        Validate(plan);
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        var filters = new List<string>();
        AppendVideo(args, filters, plan);
        const string resize = "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2,fps=10";
        if (filters.Count > 0) { filters.Add("[scene]" + resize + "[preview]"); args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[preview]"]); }
        else args.AddRange(["-map", "0:v:0", "-vf", resize]);
        args.AddRange(["-an", "-c:v", "mjpeg", "-q:v", "5", "-f", "image2pipe", "pipe:1"]);
        return args;
    }

    public static IReadOnlyList<string> Output(MediaPlan plan, string destination, bool recording)
    {
        Validate(plan);
        if (recording) destination = Path.GetFullPath(destination);
        else if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri) || uri.Scheme is not ("rtmp" or "rtmps") || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("A valid RTMP or RTMPS output URL without URL credentials is required.");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        var filters = new List<string>();
        var audioStart = AppendVideo(args, filters, plan);
        foreach (var audio in plan.Audio) args.AddRange(PlatformCapture.Arguments(plan.Platform, audio, plan.FrameRate));
        if (plan.Audio.Count > 0)
        {
            for (var i = 0; i < plan.Audio.Count; i++)
            {
                var input = plan.Audio[i];
                var gain = input.Muted ? "0" : input.Gain.ToString(CultureInfo.InvariantCulture);
                filters.Add($"[{i + audioStart}:a:0]aresample=48000,volume={gain}[a{i}]");
            }
            filters.Add(string.Concat(Enumerable.Range(0, plan.Audio.Count).Select(i => $"[a{i}]")) +
                $"amix=inputs={plan.Audio.Count}:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.95[audio]");
        }
        if (filters.Count > 0) args.AddRange(["-filter_complex", string.Join(';', filters)]);
        args.AddRange(["-map", plan.RenderScene || plan.Layers.Count > 0 ? "[scene]" : "0:v:0"]);
        if (plan.Audio.Count > 0) args.AddRange(["-map", "[audio]"]); else args.Add("-an");
        if (!plan.RenderScene && plan.Layers.Count == 0) args.AddRange(["-vf", $"scale={plan.Canvas.Width}:{plan.Canvas.Height}:force_original_aspect_ratio=decrease,pad={plan.Canvas.Width}:{plan.Canvas.Height}:(ow-iw)/2:(oh-ih)/2,format=yuv420p"]);
        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-b:v", plan.VideoBitrateKbps + "k",
            "-maxrate", plan.VideoBitrateKbps + "k", "-bufsize", (plan.VideoBitrateKbps * 2) + "k", "-g", (plan.FrameRate * 2).ToString(CultureInfo.InvariantCulture)]);
        if (plan.Audio.Count > 0) args.AddRange(["-c:a", "aac", "-b:a", plan.AudioBitrateKbps + "k", "-ar", "48000", "-ac", "2"]);
        args.AddRange(["-f", recording ? "matroska" : "flv", destination]);
        return args;
    }

    private static int AppendVideo(List<string> args, List<string> filters, MediaPlan plan)
    {
        if (!plan.RenderScene && plan.Layers.Count == 0) { args.AddRange(PlatformCapture.Arguments(plan.Platform, plan.Video, plan.FrameRate)); return 1; }
        args.AddRange(["-re", "-f", "lavfi", "-i", $"color=c=black:size={plan.Canvas.Width}x{plan.Canvas.Height}:rate={plan.FrameRate}"]);
        filters.Add("[0:v]format=rgba[background]");
        var layers = plan.Layers.Where(l => l.Visible && l.Transform.Visible && l.Transform.Opacity > 0).ToArray();
        var previous = "background";
        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i]; var t = layer.Transform;
            args.AddRange(PlatformCapture.Arguments(plan.Platform, layer.Input, plan.FrameRate));
            var width = (int)Math.Round(t.Width * t.ScaleX); var height = (int)Math.Round(t.Height * t.ScaleY);
            var crop = t.CropLeft + t.CropRight + t.CropTop + t.CropBottom > 0 ?
                $"crop=iw-{N(t.CropLeft + t.CropRight)}:ih-{N(t.CropTop + t.CropBottom)}:{N(t.CropLeft)}:{N(t.CropTop)}," : "";
            var rotation = Math.Abs(t.Rotation) > 0.0001 ? $",rotate={N(t.Rotation)}*PI/180:ow=rotw({N(t.Rotation)}*PI/180):oh=roth({N(t.Rotation)}*PI/180):c=none" : "";
            filters.Add($"[{i + 1}:v]setpts=PTS-STARTPTS,{crop}scale={width}:{height},format=rgba,colorchannelmixer=aa={N(t.Opacity)}{rotation}[layer{i}]");
            // Keep the source's centre fixed when rotation expands its bounds.
            filters.Add($"[{previous}][layer{i}]overlay=x={N(t.X)}+({width}-overlay_w)/2:y={N(t.Y)}+({height}-overlay_h)/2:eof_action=pass:shortest=0[mix{i}]");
            previous = "mix" + i;
        }
        filters.Add($"[{previous}]format=yuv420p[scene]");
        return layers.Length + 1;
    }
    private static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static void Validate(MediaPlan plan)
    {
        if (plan.Canvas.Width is < 16 or > 7680 || plan.Canvas.Height is < 16 or > 7680 || plan.Canvas.Width % 2 != 0 || plan.Canvas.Height % 2 != 0)
            throw new ArgumentException("Canvas dimensions must be even and between 16 and 7680 pixels.");
        if (plan.VideoBitrateKbps is < 100 or > 100000 || plan.AudioBitrateKbps is < 32 or > 512) throw new ArgumentException("Invalid encoding bitrate.");
        if (plan.Video.Kind is CaptureKind.Microphone or CaptureKind.DesktopAudio or CaptureKind.TestAudio or CaptureKind.PcmAudio) throw new ArgumentException("A video source is required.");
        if (plan.Audio.Any(a => a.Kind is not (CaptureKind.Microphone or CaptureKind.DesktopAudio or CaptureKind.TestAudio or CaptureKind.PcmAudio))) throw new ArgumentException("Audio inputs must be audio sources.");
        foreach (var layer in plan.Layers)
        {
            var t = layer.Transform;
            if (layer.Input.Kind is CaptureKind.Microphone or CaptureKind.DesktopAudio or CaptureKind.TestAudio or CaptureKind.PcmAudio) throw new ArgumentException("Scene layers must contain video.");
            if (new[] { t.X, t.Y, t.Width, t.Height, t.ScaleX, t.ScaleY, t.Rotation, t.Opacity, t.CropLeft, t.CropTop, t.CropRight, t.CropBottom }.Any(v => !double.IsFinite(v)) ||
                t.Width <= 0 || t.Height <= 0 || t.ScaleX <= 0 || t.ScaleY <= 0 || Math.Abs(t.X) > 32768 || Math.Abs(t.Y) > 32768 ||
                t.Width * t.ScaleX is < 1 or > 16384 || t.Height * t.ScaleY is < 1 or > 16384 || t.Opacity is < 0 or > 1 || Math.Abs(t.Rotation) > 360 ||
                new[] { t.CropLeft, t.CropTop, t.CropRight, t.CropBottom }.Any(v => v is < 0 or > 16384))
                throw new ArgumentException("Invalid scene transform.");
        }
    }
}
