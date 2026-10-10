namespace Dhole.Agent.Application.Runtime;

public sealed class MaerskCircuitOptions
{
    public const string SectionName = "MaerskCircuit";
    // Enable only after deploying the schema on each environment.
    public bool Enabled { get; set; } = false;
    public int TransientFailureThreshold { get; set; } = 3;
    public int TechnicalCooldownSeconds { get; set; } = 900;
    public int ProviderCooldownSeconds { get; set; } = 1800;

    public void Validate()
    {
        if (TransientFailureThreshold is < 2 or > 10
            || TechnicalCooldownSeconds is < 60 or > 86400
            || ProviderCooldownSeconds is < 60 or > 86400)
            throw new InvalidOperationException("Unsafe Maersk circuit settings.");
    }
}

public sealed record MaerskCircuitSnapshot(
    Guid ProviderId,
    string State,
    bool RequiresOperator,
    string? ReasonCode,
    DateTime? OpenUntilUtc,
    int ConsecutiveFailures,
    Guid? ProbeExecutionId)
{
    public static MaerskCircuitSnapshot Closed(Guid providerId)
        => new(providerId, "Closed", false, null, null, 0, null);

    // Provider-imposed restrictions do not clear automatically when a
    // cooldown passes. Technical failures allow precisely one probe.
    public bool CanSchedule(DateTime utcNow) => State switch
    {
        "Closed" => true,
        "Open" when !RequiresOperator && OpenUntilUtc <= utcNow => true,
        _ => false
    };
}
