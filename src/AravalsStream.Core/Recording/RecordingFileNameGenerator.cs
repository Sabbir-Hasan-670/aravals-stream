using AravalsStream.Core.Models;

namespace AravalsStream.Core.Recording;

public static class RecordingFileNameGenerator
{
    public static string GenerateUniqueFilePath(
        string directory,
        OutputMode mode,
        string container,
        DateTimeOffset timestamp,
        Func<string, bool>? fileExistsChecker = null)
    {
        var safeExt = container.TrimStart('.').ToLowerInvariant();
        if (safeExt is not ("mkv" or "mp4")) safeExt = "mkv";

        var suffix = mode == OutputMode.Vertical ? "_V" : "_H";
        var datePart = timestamp.ToString("yyyy-MM-dd_HH-mm-ss");
        var baseName = $"AravalsStream_{datePart}{suffix}";
        var candidate = Path.Combine(directory, $"{baseName}.{safeExt}");

        var exists = fileExistsChecker ?? File.Exists;

        int counter = 1;
        while (exists(candidate))
        {
            candidate = Path.Combine(directory, $"{baseName}_{counter}.{safeExt}");
            counter++;
        }

        return candidate;
    }
}
