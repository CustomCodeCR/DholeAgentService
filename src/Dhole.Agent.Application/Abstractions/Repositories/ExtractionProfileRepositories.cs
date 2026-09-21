using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.Abstractions.Repositories;

public interface IAgentExtractionProfileRepository : IRepository<AgentExtractionProfile, Guid>
{
    Task<IReadOnlyCollection<AgentExtractionProfile>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface IAgentExtractionRouteRepository : IRepository<AgentExtractionRoute, Guid>
{
    Task<IReadOnlyCollection<AgentExtractionRoute>> GetByProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
}
