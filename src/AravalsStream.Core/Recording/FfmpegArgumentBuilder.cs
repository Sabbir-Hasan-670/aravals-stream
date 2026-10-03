using System.Text;
using AravalsStream.Core.Recording.Models;

namespace AravalsStream.Core.Recording;

public static class FfmpegArgumentBuilder
{
    public static string Build(RecordingSettings settings, string encoderId, int width, int height,
        string videoPipeName, string audioPipeName, string output, bool streaming,
        int? outputWidth = null, int? outputHeight = null, string inputPixelFormat = "bgra")
    {
        var fps = settings.Video.FrameRate;
        var sb = new StringBuilder();
        // FFmpeg treats Windows named-pipe output paths like existing files and
        // otherwise prompts for overwrite, then exits without writing any packets.
        sb.Append("-y ");
        sb.Append("-hide_banner -loglevel warning ");
        // Keep machine-readable progress on stderr for both recordings and streams
        // so startup diagnostics can identify the first encoded output frame.
        sb.Append("-progress pipe:2 -stats_period 1 ");
        if (inputPixelFormat is not ("bgra" or "yuv420p" or "nv12")) throw new ArgumentException("Unsupported input pixel format.", nameof(inputPixelFormat));
        sb.Append($"-use_wallclock_as_timestamps 1 -f rawvideo -pix_fmt {inputPixelFormat} -s {width}x{height} -r {fps} -i \\\\.\\pipe\\{videoPipeName} ");
        sb.Append($"-use_wallclock_as_timestamps 1 -f f32le -ar 48000 -ac 2 -i \\\\.\\pipe\\{audioPipeName} ");
        var scale = outputWidth is > 0 && outputHeight is > 0 && (outputWidth != width || outputHeight != height)
            ? $"scale={outputWidth}:{outputHeight}:flags=fast_bilinear," : "";
        var vf = $"{scale}setpts=(RTCTIME-RTCSTART)/(TB*1000000)";
        sb.Append($"-vf {vf} ");
        if (!streaming)
        {
            sb.Append("-fps_mode vfr -af asetpts=(RTCTIME-RTCSTART)/(TB*1000000),aresample=async=1000:first_pts=0 ");
        }
        else
        {
            sb.Append("-fps_mode cfr -af asetpts=(RTCTIME-RTCSTART)/(TB*1000000),aresample=async=1000:first_pts=0 ");
        }
        sb.Append($"-c:v {encoderId} -b:v {settings.Video.BitrateKbps}k ");
        sb.Append(encoderId switch
        {
            "libx264" => $"-preset {settings.Video.Preset} -tune zerolatency ",
            "h264_nvenc" => "-preset p4 -tune ll ",
            "h264_qsv" => "-preset medium ",
            "h264_amf" => "-quality balanced ",
            _ => ""
        });
        sb.Append($"-g {fps * settings.Video.KeyframeIntervalSeconds} -pix_fmt yuv420p ");
        sb.Append($"-c:a aac -b:a {settings.Audio.BitrateKbps}k ");
        if (streaming) sb.Append("-rw_timeout 5000000 -f flv ");
        sb.Append('"').Append(output.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
        return sb.ToString();
    }

    public static string Redact(string message, string output)
    {
        if (string.IsNullOrEmpty(output)) return message;
        var isRtmp = Uri.TryCreate(output, UriKind.Absolute, out var outputUri) &&
                     (outputUri.Scheme.Equals("rtmp", StringComparison.OrdinalIgnoreCase) || outputUri.Scheme.Equals("rtmps", StringComparison.OrdinalIgnoreCase));
        var sanitized = message.Replace(output, isRtmp ? "[redacted RTMP destination]" : "[recording output path]", StringComparison.OrdinalIgnoreCase);
        if (!isRtmp) return sanitized;
        var slash = output.LastIndexOf('/');
        if (slash >= 0 && slash < output.Length - 1)
        {
            var key = output[(slash + 1)..];
            if (key.Length >= 4) sanitized = sanitized.Replace(key, "***REDACTED***", StringComparison.Ordinal);
        }
        return sanitized;
    }
}

