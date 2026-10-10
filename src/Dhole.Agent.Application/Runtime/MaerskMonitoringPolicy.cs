namespace Dhole.Agent.Application.Runtime;

/// <summary>Deterministic operational rules. Never attempts browser recovery.</summary>
public sealed class MaerskMonitoringOptions
{
    public const string SectionName = "MaerskMonitoring";
    // Deploy the database migration before opting in.
    public bool Enabled { get; set; } = false;
    public int PollIntervalSeconds { get; set; } = 60;
    public int QueuedWarningMinutes { get; set; } = 20;
    public int RunningWarningMinutes { get; set; } = 75;
    public int FailureWindowMinutes { get; set; } = 60;
    public int FailureCountThreshold { get; set; } = 5;
    public int HalfOpenWarningMinutes { get; set; } = 20;
    public void Validate()
    {
        if (PollIntervalSeconds is < 30 or > 3600
            || QueuedWarningMinutes is < 5 or > 1440
            || RunningWarningMinutes is < 10 or > 1440
            || FailureWindowMinutes is < 5 or > 1440
            || FailureCountThreshold is < 2 or > 100
            || HalfOpenWarningMinutes is < 5 or > 1440)
            throw new InvalidOperationException("Invalid Maersk monitoring thresholds.");
    }
}

public sealed record MaerskHealthSignals(
    bool CircuitOpenAndNeedsOperator,
    bool HalfOpenStalled,
    int BlockedProfiles,
    int LongQueued,
    int LongRunning,
    int FailuresInWindow);

public sealed record MaerskHealthFinding(string Key, string Code, string Severity);

public static class MaerskMonitoringPolicy
{
    public const string ProviderAccess = "provider-access";
    public const string HalfOpen = "half-open-stalled";
    public const string BrowserBlocked = "browser-profile-blocked";
    public const string QueueBacklog = "queue-backlog";
    public const string RunningStalled = "running-stalled";
    public const string FailedExecutions = "execution-failure-spike";

    public static IReadOnlyList<MaerskHealthFinding> Evaluate(
        MaerskHealthSignals s, MaerskMonitoringOptions options)
    {
        options.Validate();
        var alerts = new List<MaerskHealthFinding>(6);
        if (s.CircuitOpenAndNeedsOperator)
            alerts.Add(new(ProviderAccess, "maersk_provider_verification_required", "Critical"));
        if (s.HalfOpenStalled)
            alerts.Add(new(HalfOpen, "maersk_half_open_stalled", "Warning"));
        if (s.BlockedProfiles > 0)
            alerts.Add(new(BrowserBlocked, "maersk_browser_profile_blocked", "Critical"));
        if (s.LongQueued > 0)
            alerts.Add(new(QueueBacklog, "maersk_queue_delay", "Warning"));
        if (s.LongRunning > 0)
            alerts.Add(new(RunningStalled, "maersk_running_exceeds_threshold", "Warning"));
        if (s.FailuresInWindow >= options.FailureCountThreshold)
            alerts.Add(new(FailedExecutions, "maersk_recent_failure_spike", "Warning"));
        return alerts;
    }
}
