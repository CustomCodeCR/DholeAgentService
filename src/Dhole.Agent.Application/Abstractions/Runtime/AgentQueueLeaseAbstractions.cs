namespace Dhole.Agent.Application.Abstractions.Runtime;

/// <summary>
/// Durable claim storage. One PostgreSQL scope row serializes a provider/profile
/// while the job remains in the authoritative AgentExecutions table.
/// </summary>
public interface IAgentQueueLeaseStore
{
    Task<bool> TryClaimAsync(Guid executionId, string scopeKey, Guid ownerId,
        TimeSpan lease, CancellationToken cancellationToken = default);

    Task<bool> RenewAsync(string scopeKey, Guid executionId, Guid ownerId,
        TimeSpan lease, CancellationToken cancellationToken = default);

    Task ReleaseAsync(string scopeKey, Guid executionId, Guid ownerId,
        CancellationToken cancellationToken = default);

    Task<int> FailInterruptedAsync(CancellationToken cancellationToken = default);

    Task<int> FailAttemptsExhaustedAsync(CancellationToken cancellationToken = default);
}
