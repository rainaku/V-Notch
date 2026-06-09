namespace VNotch.Services;

internal readonly struct SessionSwitchInputs
{
    public bool CurrentIsPremium { get; init; }
    public bool BestIsOsCurrent { get; init; }
    public bool BestIsRecentLatestPlayback { get; init; }
    public bool CurrentStillPlaying { get; init; }
    public bool BestHasFreshTimeline { get; init; }
    public double PendingElapsedSeconds { get; init; }
}

internal static class SessionSwitchArbiter
{
    public static double HoldSeconds(bool currentIsPremium) => currentIsPremium ? 4.0 : 1.5;

    public static bool ShouldHoldCurrentSession(in SessionSwitchInputs x)
    {
        return !x.BestIsRecentLatestPlayback
            && !x.BestIsOsCurrent
            && !x.BestHasFreshTimeline
            && x.CurrentStillPlaying
            && x.PendingElapsedSeconds < HoldSeconds(x.CurrentIsPremium);
    }
}
