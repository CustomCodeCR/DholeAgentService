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
