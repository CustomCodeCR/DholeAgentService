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

public interface IAgentExtractionEquipmentRepository : IRepository<AgentExtractionEquipment, Guid>
{
    Task<IReadOnlyCollection<AgentExtractionEquipment>> GetByProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
}

public interface IAgentEndpointCaptureRepository : IRepository<AgentEndpointCapture, Guid>
{
    Task<IReadOnlyCollection<AgentEndpointCapture>> GetByProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
}

public interface IAgentExtractionFieldRepository : IRepository<AgentExtractionField, Guid>
{
    Task<IReadOnlyCollection<AgentExtractionField>> GetByProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
}
