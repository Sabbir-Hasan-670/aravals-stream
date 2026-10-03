namespace AravalsStream.Core.Streaming;

public sealed class AdaptiveBitrateController
{
    private DateTime _lastAdjustmentTime = DateTime.MinValue;
    private DateTime _lastCongestionTime = DateTime.MinValue;
    private DateTime _excellentSince = DateTime.MinValue;

    public bool Enabled { get; set; } = false;
    public bool AutoRecover { get; set; } = false;
    public int MinBitrateKbps { get; set; } = 2000;
    public int MaxBitrateKbps { get; set; } = 8000;
    public int CurrentBitrateKbps { get; private set; } = 8000;
    public int StepDownKbps { get; set; } = 1000;
    public int StepUpKbps { get; set; } = 500;
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RecoveryHoldDuration { get; set; } = TimeSpan.FromSeconds(30);

    public AdaptiveBitrateController(int initialBitrateKbps, int minBitrateKbps = 2000)
    {
        CurrentBitrateKbps = initialBitrateKbps;
        MaxBitrateKbps = initialBitrateKbps;
        MinBitrateKbps = Math.Min(minBitrateKbps, initialBitrateKbps);
    }

    public void SetTargetBitrate(int targetKbps)
    {
        MaxBitrateKbps = targetKbps;
        if (CurrentBitrateKbps > MaxBitrateKbps)
            CurrentBitrateKbps = MaxBitrateKbps;
    }

    public bool Evaluate(NetworkHealthReport report, DateTime now, out int newBitrateKbps)
    {
        newBitrateKbps = CurrentBitrateKbps;
        if (!Enabled) return false;

        // Check cooldown
        if (now - _lastAdjustmentTime < Cooldown)
            return false;

        // Congestion: step down
        if (report.State is NetworkHealthState.Unstable or NetworkHealthState.Critical)
        {
            _excellentSince = DateTime.MinValue;
            if (CurrentBitrateKbps > MinBitrateKbps)
            {
                var targetStepDown = report.State == NetworkHealthState.Critical ? StepDownKbps * 2 : StepDownKbps;
                var reduced = Math.Max(MinBitrateKbps, CurrentBitrateKbps - targetStepDown);
                if (reduced < CurrentBitrateKbps)
                {
                    CurrentBitrateKbps = reduced;
                    _lastAdjustmentTime = now;
                    newBitrateKbps = CurrentBitrateKbps;
                    return true;
                }
            }
            return false;
        }

        // Recovery: cautious step up
        if (AutoRecover && report.State == NetworkHealthState.Excellent && CurrentBitrateKbps < MaxBitrateKbps)
        {
            if (_excellentSince == DateTime.MinValue)
            {
                _excellentSince = now;
                return false;
            }

            if (now - _excellentSince >= RecoveryHoldDuration)
            {
                var increased = Math.Min(MaxBitrateKbps, CurrentBitrateKbps + StepUpKbps);
                if (increased > CurrentBitrateKbps)
                {
                    CurrentBitrateKbps = increased;
                    _lastAdjustmentTime = now;
                    _excellentSince = now; // reset recovery timer for next step
                    newBitrateKbps = CurrentBitrateKbps;
                    return true;
                }
            }
        }
        else if (report.State != NetworkHealthState.Excellent)
        {
            _excellentSince = DateTime.MinValue;
        }

        return false;
    }
}
