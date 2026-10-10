namespace Dhole.Agent.Contracts.Agents;

/// <summary>Only codes, counts and safe timestamps; no session or credential data.</summary>
public sealed record MaerskHealthAlertDto(
    Guid Id, string Key, string Code, string Severity, string State,
    DateTime FirstSeenAtUtc, DateTime LastSeenAtUtc, DateTime? ResolvedAtUtc,
    DateTime? AcknowledgedAtUtc);

public sealed record MaerskHealthSnapshotDto(
    bool MonitoringEnabled,
    DateTime ObservedAtUtc,
    int FailedInWindow,
    int QueuedOverThreshold,
    int RunningOverThreshold,
    DateTime? OldestQueuedAtUtc,
    DateTime? LastCompletedAtUtc,
    IReadOnlyCollection<MaerskHealthAlertDto> Alerts);
