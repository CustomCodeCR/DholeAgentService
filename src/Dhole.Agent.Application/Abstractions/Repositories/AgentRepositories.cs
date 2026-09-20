using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.Abstractions.Repositories;

public interface IAgentProviderRepository : IRepository<AgentProvider, Guid>
{
    Task<AgentProvider?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
    Task<bool> ExistsByCodeAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<AgentProvider>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface IAgentDefinitionRepository : IRepository<AgentDefinition, Guid>
{
    Task<AgentDefinition?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
    Task<bool> ExistsByCodeAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<AgentDefinition>> GetAllAsync(Guid? providerId = null, CancellationToken cancellationToken = default);
}

public interface IAgentCredentialRepository : IRepository<AgentCredential, Guid>
{
    Task<IReadOnlyCollection<AgentCredential>> GetAllAsync(Guid? providerId = null, CancellationToken cancellationToken = default);
}

public interface IBrowserProfileRepository : IRepository<BrowserProfile, Guid>
{
    Task<BrowserProfile?> GetByProviderCredentialAsync(Guid providerId, Guid credentialId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<BrowserProfile>> GetAllAsync(Guid? providerId = null, CancellationToken cancellationToken = default);
}

public interface IAgentScheduleRepository : IRepository<AgentSchedule, Guid>
{
    Task<IReadOnlyCollection<AgentSchedule>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<AgentSchedule>> GetDueAsync(DateTime utcNow, int take, CancellationToken cancellationToken = default);
}

public interface IAgentExecutionRepository : IRepository<AgentExecution, Guid>
{
    Task<IReadOnlyCollection<AgentExecution>> GetRecentAsync(int take, AgentExecutionStatus? status = null, CancellationToken cancellationToken = default);
}

public interface IAgentResultRepository : IRepository<AgentResult, Guid>
{
    Task<AgentResult?> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default);
}
