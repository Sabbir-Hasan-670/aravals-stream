using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using AravalsStream.Core.Versioning;

namespace AravalsStream.App.Services;

public sealed record RelayDiagnosticSnapshot(bool Enabled, string RelayUrl, string ConnectionState,
    int ReconnectCount, DateTimeOffset? LastConnected, DateTimeOffset? LastEvent,
    long EventsReceived, long EventsRejected, string KickSubscriptionState);

public static class DiagnosticsExporter
{
    public static async Task<string> ExportZipAsync(
        AppSettings settings,
        FfmpegResolution ffmpeg,
        IReadOnlyList<EncoderInfo> encoders,
        string? destinationZipPath = null,
        RelayDiagnosticSnapshot? relayDiagnostics = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"AravalsDiag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. System info
            var sysInfo = new
            {
                Application = AppVersion.Name,
                Version = AppVersion.Version,
                BuildDate = AppVersion.BuildDate,
                OSVersion = Environment.OSVersion.ToString(),
                DotNetVersion = Environment.Version.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
                WorkingSetMb = Environment.WorkingSet / (1024 * 1024)
            };
            await File.WriteAllTextAsync(Path.Combine(tempDir, "system_info.json"), JsonSerializer.Serialize(sysInfo, new JsonSerializerOptions { WriteIndented = true }));

            // 2. FFmpeg info
            var ffmpegInfo = new
            {
                Status = ffmpeg.Status.ToString(),
                Path = ffmpeg.FfmpegPath,
                Version = ffmpeg.Version,
                Error = ffmpeg.ErrorMessage,
                AvailableEncoders = encoders.Select(e => new { e.Id, e.DisplayName, e.HardwareAccelerated, e.Vendor, e.Available, e.Recommended })
            };
            await File.WriteAllTextAsync(Path.Combine(tempDir, "ffmpeg_info.json"), JsonSerializer.Serialize(ffmpegInfo, new JsonSerializerOptions { WriteIndented = true }));

            // 3. Sanitized settings (zero secrets)
            var sanitizedSettings = new
            {
                settings.SettingsSchemaVersion,
                settings.PreviewMode,
                General = settings.General,
                Streaming = settings.Streaming,
                Recording = new
                {
                    settings.Recording.Container,
                    settings.Recording.Mode,
                    Video = settings.Recording.Video,
                    Audio = settings.Recording.Audio
                },
                DestinationGroups = settings.DestinationGroups.Select(g => new
                {
                    g.Platform,
                    g.Name,
                    g.Enabled,
                    g.Routing,
                    g.Status,
                    Horizontal = new { g.Horizontal.Name, g.Horizontal.VideoBitrateKbps, g.Horizontal.AudioBitrateKbps, g.Horizontal.FrameRate, g.Horizontal.OutputMode },
                    Vertical = new { g.Vertical.Name, g.Vertical.VideoBitrateKbps, g.Vertical.AudioBitrateKbps, g.Vertical.FrameRate, g.Vertical.OutputMode }
                }),
                Scenes = settings.Scenes.Select(s => new { s.Name, SourceCount = s.Sources.Count }),
                Relay = relayDiagnostics is null
                    ? new RelayDiagnosticSnapshot(settings.Relay.Enabled, SanitizeRelayUrl(settings.Relay.RelayUrl), "Not captured", 0, null, null, 0, 0, settings.Relay.KickSubscriptionState.ToString())
                    : relayDiagnostics with { RelayUrl = SanitizeRelayUrl(relayDiagnostics.RelayUrl) }
            };
            await File.WriteAllTextAsync(Path.Combine(tempDir, "settings_sanitized.json"), JsonSerializer.Serialize(sanitizedSettings, new JsonSerializerOptions { WriteIndented = true }));

            // 4. Recent logs
            var logs = AppLog.GetRecentLogText(500);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "recent_logs.txt"), logs);

            // 5. Package into ZIP
            var zipPath = destinationZipPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"AravalsStream-Diagnostics-{DateTime.Now:yyyyMMdd_HHmmss}.zip");

            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(tempDir, zipPath);

            AppLog.Write("Diagnostics", $"Diagnostics exported successfully to {zipPath}");
            return zipPath;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static string SanitizeRelayUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "(invalid URL)";
        return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path);
    }
}
