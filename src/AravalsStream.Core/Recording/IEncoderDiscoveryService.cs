using System.Diagnostics;
using AravalsStream.Core.Recording.Models;

namespace AravalsStream.Core.Recording;

public interface IEncoderDiscoveryService
{
    IReadOnlyList<EncoderInfo> DetectEncoders(string ffmpegPath);
    EncoderInfo ResolveEncoder(string requestedId, IReadOnlyList<EncoderInfo> availableEncoders);
}

public sealed class EncoderDiscoveryService : IEncoderDiscoveryService
{
    private readonly Func<string, string, (bool Success, string Output, string Error)> _commandRunner;

    public EncoderDiscoveryService() : this(DefaultRunCommand) { }

    public EncoderDiscoveryService(Func<string, string, (bool Success, string Output, string Error)> commandRunner)
    {
        _commandRunner = commandRunner;
    }

    public IReadOnlyList<EncoderInfo> DetectEncoders(string ffmpegPath)
    {
        var result = new List<EncoderInfo>();

        var (success, output, _) = _commandRunner(ffmpegPath, "-encoders");
        if (!success || string.IsNullOrWhiteSpace(output))
        {
            // Default fallback if query fails
            return [new EncoderInfo("libx264", "Software (libx264)", false, "Software", true, true)];
        }

        bool hasNvenc = output.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase);
        bool hasQsv = output.Contains("h264_qsv", StringComparison.OrdinalIgnoreCase);
        bool hasAmf = output.Contains("h264_amf", StringComparison.OrdinalIgnoreCase);
        bool hasX264 = output.Contains("libx264", StringComparison.OrdinalIgnoreCase);

        // Test actual hardware initialization to avoid offering broken hardware encoders
        bool nvencWorking = hasNvenc && TestEncoder(ffmpegPath, "h264_nvenc");
        bool qsvWorking = hasQsv && TestEncoder(ffmpegPath, "h264_qsv");
        bool amfWorking = hasAmf && TestEncoder(ffmpegPath, "h264_amf");
        bool x264Working = hasX264; // Software encoder is always reliable if present

        // Determine recommended encoder (NVENC > QSV > AMF > libx264)
        string recommendedId = nvencWorking ? "h264_nvenc"
            : qsvWorking ? "h264_qsv"
            : amfWorking ? "h264_amf"
            : "libx264";

        if (hasNvenc)
        {
            result.Add(new EncoderInfo(
                "h264_nvenc",
                "NVIDIA NVENC H.264",
                true,
                "NVIDIA",
                nvencWorking,
                recommendedId == "h264_nvenc"));
        }

        if (hasQsv)
        {
            result.Add(new EncoderInfo(
                "h264_qsv",
                "Intel Quick Sync H.264",
                true,
                "Intel",
                qsvWorking,
                recommendedId == "h264_qsv"));
        }

        if (hasAmf)
        {
            result.Add(new EncoderInfo(
                "h264_amf",
                "AMD AMF H.264",
                true,
                "AMD",
                amfWorking,
                recommendedId == "h264_amf"));
        }

        if (hasX264)
        {
            result.Add(new EncoderInfo(
                "libx264",
                "Software (libx264)",
                false,
                "Software",
                x264Working,
                recommendedId == "libx264"));
        }

        if (result.Count == 0)
        {
            result.Add(new EncoderInfo("libx264", "Software (libx264)", false, "Software", true, true));
        }

        return result;
    }

    public EncoderInfo ResolveEncoder(string requestedId, IReadOnlyList<EncoderInfo> availableEncoders)
    {
        if (string.Equals(requestedId, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var recommended = availableEncoders.FirstOrDefault(e => e.Recommended && e.Available);
            if (recommended is not null) return recommended;

            var firstAvailable = availableEncoders.FirstOrDefault(e => e.Available);
            if (firstAvailable is not null) return firstAvailable;
        }
        else
        {
            var match = availableEncoders.FirstOrDefault(e =>
                string.Equals(e.Id, requestedId, StringComparison.OrdinalIgnoreCase));
            if (match is not null && match.Available) return match;
        }

        // Fallback to software x264
        var x264 = availableEncoders.FirstOrDefault(e => e.Id == "libx264");
        return x264 ?? new EncoderInfo("libx264", "Software (libx264)", false, "Software", true, true);
    }

    private bool TestEncoder(string ffmpegPath, string encoderId)
    {
        // Fast 1-frame null encoding test to verify GPU driver and encoder device can actually initialize
        var args = $"-hide_banner -f lavfi -i color=c=black:s=256x256:d=0.04 -c:v {encoderId} -f null -";
        var (success, _, _) = _commandRunner(ffmpegPath, args);
        return success;
    }

    private static (bool Success, string Output, string Error) DefaultRunCommand(string exePath, string arguments)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(); } catch { }
                return (false, string.Empty, "Timeout");
            }
            return (proc.ExitCode == 0, output, error);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}
