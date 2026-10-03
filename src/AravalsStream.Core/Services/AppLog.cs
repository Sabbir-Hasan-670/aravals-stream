using System.Text.RegularExpressions;

namespace AravalsStream.Core.Services;

public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AravalsStream", "logs");

    private static readonly string MainLogPath = Path.Combine(LogDir, "application.log");

    private static readonly Regex SecretRegex = new(
        @"(rtmp[s]?:\/\/[^\/\s]+\/[^\/\s]+\/)([^\s]+)|((?:key|token|password|secret|bearer|verifier)[=:\s]+)[^\s&]+|\bEAA[A-Za-z0-9]{20,}\b|\b(?:act|rft)\.[A-Za-z0-9\-_]{16,}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static AppLog()
    {
        try { CleanupOldLogs(14); } catch { }
    }

    public static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        return SecretRegex.Replace(message, match =>
        {
            if (match.Groups[1].Success)
                return match.Groups[1].Value + "[REDACTED]";
            if (match.Groups[3].Success)
                return match.Groups[3].Value + "[REDACTED]";
            return "[REDACTED]";
        });
    }

    public static void Write(string category, string message)
    {
        try
        {
            var sanitized = Sanitize(message);
            var now = DateTimeOffset.Now;
            var line = $"{now:O} [{category}] {sanitized}{Environment.NewLine}";

            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);

                // Write to main log (application.log)
                File.AppendAllText(MainLogPath, line);

                // Write to daily rolling log (AravalsStream-yyyy-MM-dd.log)
                var dailyPath = Path.Combine(LogDir, $"AravalsStream-{now:yyyy-MM-dd}.log");
                File.AppendAllText(dailyPath, line);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void CleanupOldLogs(int retentionDays = 14)
    {
        if (!Directory.Exists(LogDir)) return;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        foreach (var file in Directory.GetFiles(LogDir, "AravalsStream-*.log"))
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.LastWriteTimeUtc < cutoff)
                {
                    fi.Delete();
                }
            }
            catch { }
        }
    }

    public static string GetRecentLogText(int lineCount = 500)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(MainLogPath)) return string.Empty;
                var lines = File.ReadAllLines(MainLogPath);
                var takeCount = Math.Min(lines.Length, lineCount);
                return string.Join(Environment.NewLine, lines.TakeLast(takeCount));
            }
        }
        catch
        {
            return string.Empty;
        }
    }
}
