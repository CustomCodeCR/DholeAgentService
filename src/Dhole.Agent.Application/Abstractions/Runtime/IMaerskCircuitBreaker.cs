using Dhole.Agent.Application.Runtime;

namespace Dhole.Agent.Application.Abstractions.Runtime;

/// <summary>
/// Provider-wide durable stop signal. Never uses browser profile/session
/// rotation to recover from a provider challenge.
/// </summary>
public interface IMaerskCircuitBreaker
{
    Task<MaerskCircuitSnapshot> GetAsync(Guid providerId, CancellationToken ct = default);
    Task<bool> CanScheduleAsync(Guid providerId, CancellationToken ct = default);
    Task<bool> TryEnterAsync(Guid providerId, Guid executionId, CancellationToken ct = default);
    Task RecordFailureAsync(Guid providerId, Guid executionId, string errorCode, CancellationToken ct = default);
    Task RecordSuccessAsync(Guid providerId, Guid executionId, CancellationToken ct = default);
    Task<bool> ResetByOperatorAsync(Guid providerId, Guid actorId, string reason,
        bool verifiedWithProvider, CancellationToken ct = default);
}
