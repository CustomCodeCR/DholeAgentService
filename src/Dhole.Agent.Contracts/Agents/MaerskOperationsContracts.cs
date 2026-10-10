namespace Dhole.Agent.Contracts.Agents;

// Deliberately omit profile storage paths, credentials, cookies, prompts,
// raw provider response bodies and potentially sensitive error messages.
public sealed record MaerskOperationCircuitDto(
    bool FeatureEnabled, string State, bool RequiresOperator,
    string? ReasonCode, DateTime? OpenUntilUtc,
    int ConsecutiveFailures, Guid? ProbeExecutionId)
{
    public DateTime? UpdatedAtUtc { get; init; }
    public string? PersistedState { get; init; }
    public string ConfigurationSource { get; init; } = "Default";
}

public sealed record MaerskOperationCountersDto(
    int Queued, int Running, int WaitingForAuthentication,
    int Completed, int Failed);

public sealed record MaerskOperationProfileDto(
    Guid Id, string Name, string Status,
    DateTime? LastLoginAt, DateTime? LastUsedAt,
    DateTime? SessionExpiresAt);

public sealed record MaerskOperationExecutionDto(
    Guid Id, string Status, string ExecutionType, int Attempt, int MaxAttempts,
    string? ErrorCode, DateTime CreatedAtUtc, DateTime? StartedAt,
    DateTime? CompletedAt, string CorrelationId);

public sealed record MaerskOperationEventDto(
    Guid Id, string EventType, string? ReasonCode,
    Guid? ActorId, DateTime OccurredAtUtc);

public sealed record MaerskOperationsDto(
    Guid ProviderId, string ProviderName, DateTime GeneratedAtUtc,
    MaerskOperationCircuitDto Circuit, MaerskOperationCountersDto Counters,
    IReadOnlyCollection<MaerskOperationProfileDto> Profiles,
    IReadOnlyCollection<MaerskOperationExecutionDto> Executions,
    IReadOnlyCollection<MaerskOperationEventDto> Events,
    MaerskHealthSnapshotDto Monitoring);

public sealed record MaerskProfileHealthDto(
    string Environment, Guid ProviderId, Guid ProfileId, string ProfileName,
    string ProfileStatus, bool IsActive, string CircuitState, bool RequiresOperator,
    string? ErrorCode, DateTime? LastLoginAt, DateTime? LastUsedAt,
    DateTime? SessionExpiresAt, DateTime? LastSuccessAtUtc,
    int Queued, int Running, int WaitingForAuthentication, string NextAction);
