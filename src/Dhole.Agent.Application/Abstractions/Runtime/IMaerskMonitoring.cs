using Dhole.Agent.Contracts.Agents;

namespace Dhole.Agent.Application.Abstractions.Runtime;

public interface IMaerskMonitoring
{
    Task EvaluateAsync(CancellationToken ct = default);
    Task<MaerskHealthSnapshotDto> GetSnapshotAsync(Guid providerId, CancellationToken ct = default);
    Task<bool> AcknowledgeAsync(Guid providerId, Guid alertId, Guid actorId,
        CancellationToken ct = default);
}
