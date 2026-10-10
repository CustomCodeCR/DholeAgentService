namespace Dhole.Agent.Persistence.Repositories;

public sealed class MaerskCircuitRecord
{
    public Guid ProviderId { get; set; }
    public string State { get; set; } = "Closed";
    public bool RequiresOperator { get; set; }
    public string? ReasonCode { get; set; }
    public DateTime? OpenUntilUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public Guid? ProbeExecutionId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
