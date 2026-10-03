namespace AravalsStream.Core.Services;

public sealed class SessionRecoveryService
{
    private readonly string SentinelPath;
    public SessionRecoveryService(string? sentinelPath = null) => SentinelPath = sentinelPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AravalsStream", "session.active");

    public bool WasPreviousShutdownUnclean { get; private set; }

    public void CheckAndStartSession()
    {
        try
        {
            if (File.Exists(SentinelPath))
            {
                WasPreviousShutdownUnclean = true;
                AppLog.Write("Recovery", "Detected previous unclean session shutdown.");
            }
            else
            {
                WasPreviousShutdownUnclean = false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(SentinelPath)!);
            File.WriteAllText(SentinelPath, $"{DateTime.UtcNow:O} PID={Environment.ProcessId}");
        }
        catch (Exception ex)
        {
            AppLog.Write("Recovery", $"Failed to update session sentinel: {ex.Message}");
        }
    }

    public void MarkCleanShutdown()
    {
        try
        {
            if (File.Exists(SentinelPath))
            {
                File.Delete(SentinelPath);
            }
            AppLog.Write("Recovery", "Session marked as cleanly closed.");
        }
        catch (Exception ex)
        {
            AppLog.Write("Recovery", $"Failed to remove session sentinel: {ex.Message}");
        }
    }
}
