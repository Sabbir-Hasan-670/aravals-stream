namespace AravalsStream.Core.Settings;

public enum PerformanceMode { Auto, Eco, Balanced, Quality, Custom }
public enum HardwareClass { Low, Moderate, High }

public sealed class PerformanceSettings
{
    public PerformanceMode Mode { get; set; } = PerformanceMode.Auto;
    public bool AutomaticProtection { get; set; } = true;
    public bool AllowAutomaticStreamQualityReduction { get; set; }
    public int CustomPreviewFps { get; set; } = 30;
    public int CustomMeterRefreshHz { get; set; } = 30;
}

public sealed record PerformanceProfile(int PreviewFps, int MeterRefreshHz, int StatsRefreshMilliseconds,
    bool ReducedAnimations);

public static class PerformancePolicy
{
    public static bool ShouldWarnBeforeStarting(HardwareClass hardware, int outputCount, double equivalent1080p60) =>
        (hardware == HardwareClass.Low && equivalent1080p60 > 1.5) ||
        (hardware == HardwareClass.Moderate && (equivalent1080p60 >= 3.0 || outputCount >= 4)) || outputCount >= 5;

    public static HardwareClass Classify(int logicalCores, long memoryBytes, bool hardwareEncoder)
    {
        var gib = memoryBytes / (1024.0 * 1024 * 1024);
        if (logicalCores <= 4 || gib < 8 || (!hardwareEncoder && logicalCores <= 6)) return HardwareClass.Low;
        if (logicalCores >= 12 && gib >= 16 && hardwareEncoder) return HardwareClass.High;
        return HardwareClass.Moderate;
    }

    public static PerformanceProfile Resolve(PerformanceSettings settings, HardwareClass hardware,
        bool hidden, bool overloaded, bool hasActiveOutputs = true)
    {
        var mode = settings.Mode == PerformanceMode.Auto
            ? hardware == HardwareClass.Low ? PerformanceMode.Eco : hardware == HardwareClass.High ? PerformanceMode.Quality : PerformanceMode.Balanced
            : settings.Mode;
        var profile = mode switch
        {
            PerformanceMode.Eco => new PerformanceProfile(15, 30, 1000, true),
            PerformanceMode.Quality => new PerformanceProfile(hasActiveOutputs ? 60 : 30, 60, 500, false),
            PerformanceMode.Custom => new PerformanceProfile(Math.Clamp(settings.CustomPreviewFps, 1, 60),
                Math.Clamp(settings.CustomMeterRefreshHz, 30, 60), 1000, false),
            _ => new PerformanceProfile(hasActiveOutputs ? 30 : 15, 30, 750, false)
        };
        if (hidden) return profile with { PreviewFps = 0, MeterRefreshHz = 2, StatsRefreshMilliseconds = 2000, ReducedAnimations = true };
        if (overloaded && settings.AutomaticProtection)
            return profile with { PreviewFps = Math.Min(profile.PreviewFps, 10), ReducedAnimations = true };
        return profile;
    }
}
