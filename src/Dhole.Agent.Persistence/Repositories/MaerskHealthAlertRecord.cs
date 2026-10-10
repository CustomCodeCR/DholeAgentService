namespace Dhole.Agent.Persistence.Repositories;

public sealed class MaerskHealthAlertRecord
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string AlertKey { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string State { get; set; } = "Active";
    public int Occurrences { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public Guid? AcknowledgedBy { get; set; }
}
