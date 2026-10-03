using AravalsStream.Core.Recording.Models;

namespace AravalsStream.Core.Streaming;

public enum NetworkHealthState
{
    Excellent,
    Good,
    Unstable,
    Critical
}

public sealed class NetworkHealthReport
{
    public NetworkHealthState State { get; set; } = NetworkHealthState.Excellent;
    public int TargetBitrateKbps { get; set; }
    public double? MeasuredBitrateKbps { get; set; }
    public double BitrateRatio { get; set; } = 1.0;
    public long DroppedFrames { get; set; }
    public double DropRatePercent { get; set; }
    public double CurrentFps { get; set; }
    public double TargetFps { get; set; }
    public int ReconnectCount { get; set; }
    public string Message { get; set; } = "Network health is optimal.";
    public int SuggestedBitrateKbps { get; set; }
    public double RealtimeRatio { get; set; } = 1.0;
    public MediaCadenceState CadenceState { get; set; } = MediaCadenceState.Healthy;
}

public static class NetworkHealthEvaluator
{
    public static NetworkHealthReport Evaluate(
        int targetBitrateKbps,
        double? measuredBitrateKbps,
        long droppedFrames,
        long totalFrames,
        double currentFps,
        double targetFps,
        int reconnectCount,
        bool isReconnecting,
        double realtimeRatio = 1.0,
        MediaCadenceState cadenceState = MediaCadenceState.Healthy)
    {
        var report = new NetworkHealthReport
        {
            TargetBitrateKbps = targetBitrateKbps,
            MeasuredBitrateKbps = measuredBitrateKbps,
            DroppedFrames = droppedFrames,
            CurrentFps = currentFps,
            TargetFps = targetFps,
            ReconnectCount = reconnectCount,
            SuggestedBitrateKbps = targetBitrateKbps,
            RealtimeRatio = realtimeRatio,
            CadenceState = cadenceState
        };

        if (totalFrames > 0 && droppedFrames > 0)
        {
            report.DropRatePercent = Math.Clamp((double)droppedFrames / (totalFrames + droppedFrames) * 100.0, 0, 100);
        }

        if (isReconnecting)
        {
            report.State = NetworkHealthState.Critical;
            report.Message = "Connection lost. Reconnecting to ingest server...";
            report.SuggestedBitrateKbps = Math.Max(1500, (int)(targetBitrateKbps * 0.7));
            return report;
        }

        if (measuredBitrateKbps.HasValue && targetBitrateKbps > 0)
        {
            report.BitrateRatio = Math.Clamp(measuredBitrateKbps.Value / targetBitrateKbps, 0, 2.0);
        }
        else
        {
            report.BitrateRatio = 1.0;
        }

        double fpsRatio = targetFps > 0 ? Math.Clamp(currentFps / targetFps, 0, 1.5) : 1.0;

        // Health state determination
        if (report.DropRatePercent > 15.0 || (measuredBitrateKbps.HasValue && report.BitrateRatio < 0.50) || cadenceState == MediaCadenceState.Critical || realtimeRatio < 0.75)
        {
            report.State = NetworkHealthState.Critical;
            report.Message = cadenceState == MediaCadenceState.Critical || realtimeRatio < 0.75
                ? $"Critical media cadence lag: stream is behind real time (ratio {realtimeRatio:0.00})."
                : $"Severe network congestion: {report.DropRatePercent:0.1}% dropped frames, upload at {report.BitrateRatio * 100:0}% of target.";
            report.SuggestedBitrateKbps = Math.Max(1500, (int)(targetBitrateKbps * 0.65));
        }
        else if (report.DropRatePercent > 5.0 || (measuredBitrateKbps.HasValue && report.BitrateRatio < 0.75) || fpsRatio < 0.85 || cadenceState == MediaCadenceState.Degraded || realtimeRatio < 0.90)
        {
            report.State = NetworkHealthState.Unstable;
            report.Message = cadenceState == MediaCadenceState.Degraded || realtimeRatio < 0.90
                ? $"Media cadence lag: stream is falling behind real time (ratio {realtimeRatio:0.00})."
                : $"Network unstable: upload at {report.BitrateRatio * 100:0}% of target bitrate ({measuredBitrateKbps ?? 0:0} kbps).";
            report.SuggestedBitrateKbps = Math.Max(1500, (int)(targetBitrateKbps * 0.80));
        }
        else if ((measuredBitrateKbps.HasValue && report.BitrateRatio < 0.90) || report.DropRatePercent > 1.0)
        {
            report.State = NetworkHealthState.Good;
            report.Message = "Network is good with minor bandwidth fluctuations.";
            report.SuggestedBitrateKbps = targetBitrateKbps;
        }
        else
        {
            report.State = NetworkHealthState.Excellent;
            report.Message = "Network health is optimal. Full bitrate sustained.";
            report.SuggestedBitrateKbps = targetBitrateKbps;
        }

        return report;
    }
}
