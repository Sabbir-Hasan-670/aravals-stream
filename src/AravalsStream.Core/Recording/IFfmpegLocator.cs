using System.Diagnostics;
using AravalsStream.Core.Recording.Models;

namespace AravalsStream.Core.Recording;

public interface IFfmpegLocator
{
    FfmpegResolution Locate(string? configuredPath = null);
}

public sealed class FfmpegLocator : IFfmpegLocator
{
    private readonly Func<string, (bool Success, string Output, string Error)> _runner;

    public FfmpegLocator() : this(DefaultRunProcess) { }

    public FfmpegLocator(Func<string, (bool Success, string Output, string Error)> runner)
    {
        _runner = runner;
    }

    public FfmpegResolution Locate(string? configuredPath = null)
    {
        var candidates = GetCandidates(configuredPath);

        foreach (var (candidateFfmpeg, source) in candidates)
        {
            if (!File.Exists(candidateFfmpeg)) continue;

            var (success, output, error) = _runner(candidateFfmpeg);
            if (success)
            {
                var version = ExtractVersion(output);
                var candidateProbe = ResolveFfprobe(candidateFfmpeg);
                return new FfmpegResolution(
                    FfmpegStatus.Available,
                    Path.GetFullPath(candidateFfmpeg),
                    candidateProbe,
                    version,
                    null);
            }
            else if (!string.IsNullOrWhiteSpace(configuredPath) && candidateFfmpeg == configuredPath)
            {
                return new FfmpegResolution(
                    FfmpegStatus.Invalid,
                    candidateFfmpeg,
                    null,
                    null,
                    $"Configured FFmpeg at '{candidateFfmpeg}' failed to execute: {error}");
            }
        }

        return new FfmpegResolution(
            FfmpegStatus.Missing,
            null,
            null,
            null,
            "FFmpeg executable not found. Please install FFmpeg or configure its path in Settings.");
    }

    private static IEnumerable<(string Path, string Source)> GetCandidates(string? configuredPath)
    {
        // 1. Bundled application FFmpeg
        var baseDir = AppContext.BaseDirectory;
        yield return (Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe"), "bundled");
        yield return (Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"), "bundled");
        yield return (Path.Combine(baseDir, "ffmpeg.exe"), "bundled");

        // Also check workspace root / parent folders for developer / bundled folder
        var parentDir = new DirectoryInfo(baseDir);
        while (parentDir != null)
        {
            var candidate = Path.Combine(parentDir.FullName, "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(candidate)) yield return (candidate, "bundled");
            parentDir = parentDir.Parent;
        }

        // 2. Configured path
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var cleaned = configuredPath.Trim('\"', ' ');
            if (Directory.Exists(cleaned))
            {
                yield return (Path.Combine(cleaned, "ffmpeg.exe"), "configured");
                yield return (Path.Combine(cleaned, "bin", "ffmpeg.exe"), "configured");
            }
            else
            {
                yield return (cleaned, "configured");
            }
        }

        // 3. System PATH & Common locations
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in paths)
        {
            yield return (Path.Combine(p, "ffmpeg.exe"), "path");
        }

        // Common Windows fallbacks
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return (Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe"), "winget");
        yield return (Path.Combine(localAppData, "ffmpeg", "bin", "ffmpeg.exe"), "user-local");
        yield return (@"C:\ffmpeg\bin\ffmpeg.exe", "common");
        yield return (@"C:\ProgramData\chocolatey\bin\ffmpeg.exe", "choco");
    }

    private static string? ResolveFfprobe(string ffmpegPath)
    {
        var dir = Path.GetDirectoryName(ffmpegPath);
        if (!string.IsNullOrEmpty(dir))
        {
            var probeSameDir = Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(probeSameDir)) return Path.GetFullPath(probeSameDir);
        }

        // Search PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var p in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(p, "ffprobe.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static string ExtractVersion(string output)
    {
        // First line of ffmpeg -version usually: "ffmpeg version 9.0.2-essentials_build-www.gyan.dev Copyright..."
        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        if (firstLine.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))
        {
            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3) return parts[2];
        }
        return string.IsNullOrWhiteSpace(firstLine) ? "Unknown" : firstLine;
    }

    private static (bool Success, string Output, string Error) DefaultRunProcess(string exePath)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "-version",
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
                return (false, string.Empty, "Timeout executing ffmpeg -version");
            }
            return (proc.ExitCode == 0, output, error);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}
