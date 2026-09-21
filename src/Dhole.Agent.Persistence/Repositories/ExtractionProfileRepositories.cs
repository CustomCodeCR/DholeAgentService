using CustomCodeFramework.Postgres.EntityFramework.Repositories;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.Persistence.Repositories;

public sealed class AgentExtractionProfileRepository(ServiceDbContext dbContext)
    : EfRepository<AgentExtractionProfile, Guid>(dbContext), IAgentExtractionProfileRepository
{
    public async Task<IReadOnlyCollection<AgentExtractionProfile>> GetAllAsync(CancellationToken cancellationToken = default)
        => await dbContext.AgentExtractionProfiles.AsNoTracking().Where(x => !x.IsDeleted).OrderBy(x => x.Name).ToListAsync(cancellationToken);
}

public sealed class AgentExtractionRouteRepository(ServiceDbContext dbContext)
    : EfRepository<AgentExtractionRoute, Guid>(dbContext), IAgentExtractionRouteRepository
{
    public async Task<IReadOnlyCollection<AgentExtractionRoute>> GetByProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
        => await dbContext.AgentExtractionRoutes.AsNoTracking()
            .Where(x => x.ProfileId == profileId && !x.IsDeleted)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.PolName)
            .ThenBy(x => x.PodName)
            .ToListAsync(cancellationToken);
}

public sealed class AgentExtractionEquipmentRepository(ServiceDbContext dbContext)
    : EfRepository<AgentExtractionEquipment, Guid>(dbContext), IAgentExtractionEquipmentRepository
{
    public async Task<IReadOnlyCollection<AgentExtractionEquipment>> GetByProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
        => await dbContext.AgentExtractionEquipment.AsNoTracking()
            .Where(x => x.ProfileId == profileId && !x.IsDeleted)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Code)
            .ToListAsync(cancellationToken);
}

public sealed class AgentEndpointCaptureRepository(ServiceDbContext dbContext)
    : EfRepository<AgentEndpointCapture, Guid>(dbContext), IAgentEndpointCaptureRepository
{
    public async Task<IReadOnlyCollection<AgentEndpointCapture>> GetByProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
        => await dbContext.AgentEndpointCaptures.AsNoTracking()
            .Where(x => x.ProfileId == profileId && !x.IsDeleted)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
}
