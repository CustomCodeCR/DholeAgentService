namespace Dhole.Agent.Persistence.Repositories;

/// <summary>Read-only operational record. Event bodies never contain secrets.</summary>
public sealed class MaerskCircuitEventRecord
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid? ExecutionId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public Guid? ActorId { get; set; }
    public string? OperatorReason { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}
