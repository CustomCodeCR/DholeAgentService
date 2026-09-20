using CustomCodeFramework.Postgres.EntityFramework.Repositories;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.Persistence.Repositories;

public sealed class AgentProviderRepository(ServiceDbContext dbContext)
    : EfRepository<AgentProvider, Guid>(dbContext), IAgentProviderRepository
{
    public Task<AgentProvider?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var value = code.Trim().ToUpperInvariant();
        return dbContext.AgentProviders.FirstOrDefaultAsync(x => x.Code == value && !x.IsDeleted, cancellationToken);
    }

    public Task<bool> ExistsByCodeAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default)
    {
        var value = code.Trim().ToUpperInvariant();
        return dbContext.AgentProviders.AnyAsync(x => x.Code == value && (!excludeId.HasValue || x.Id != excludeId), cancellationToken);
    }

    public async Task<IReadOnlyCollection<AgentProvider>> GetAllAsync(CancellationToken cancellationToken = default)
        => await dbContext.AgentProviders.AsNoTracking().Where(x => !x.IsDeleted).OrderBy(x => x.Name).ToListAsync(cancellationToken);
}

public sealed class AgentDefinitionRepository(ServiceDbContext dbContext)
    : EfRepository<AgentDefinition, Guid>(dbContext), IAgentDefinitionRepository
{
    public Task<AgentDefinition?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var value = code.Trim().ToUpperInvariant();
        return dbContext.AgentDefinitions.FirstOrDefaultAsync(x => x.Code == value && !x.IsDeleted, cancellationToken);
    }

    public Task<bool> ExistsByCodeAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default)
    {
        var value = code.Trim().ToUpperInvariant();
        return dbContext.AgentDefinitions.AnyAsync(x => x.Code == value && (!excludeId.HasValue || x.Id != excludeId), cancellationToken);
    }

    public async Task<IReadOnlyCollection<AgentDefinition>> GetAllAsync(Guid? providerId = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AgentDefinitions.AsNoTracking().Where(x => !x.IsDeleted);
        if (providerId.HasValue) query = query.Where(x => x.ProviderId == providerId.Value);
        return await query.OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }
}

public sealed class AgentCredentialRepository(ServiceDbContext dbContext)
    : EfRepository<AgentCredential, Guid>(dbContext), IAgentCredentialRepository
{
    public async Task<IReadOnlyCollection<AgentCredential>> GetAllAsync(Guid? providerId = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AgentCredentials.AsNoTracking().Where(x => !x.IsDeleted);
        if (providerId.HasValue) query = query.Where(x => x.ProviderId == providerId.Value);
        return await query.OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }
}

public sealed class BrowserProfileRepository(ServiceDbContext dbContext)
    : EfRepository<BrowserProfile, Guid>(dbContext), IBrowserProfileRepository
{
    public Task<BrowserProfile?> GetByProviderCredentialAsync(Guid providerId, Guid credentialId, CancellationToken cancellationToken = default)
        => dbContext.BrowserProfiles.FirstOrDefaultAsync(
            x => x.ProviderId == providerId && x.CredentialId == credentialId && !x.IsDeleted, cancellationToken);

    public async Task<IReadOnlyCollection<BrowserProfile>> GetAllAsync(Guid? providerId = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.BrowserProfiles.AsNoTracking().Where(x => !x.IsDeleted);
        if (providerId.HasValue) query = query.Where(x => x.ProviderId == providerId.Value);
        return await query.OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }
}

public sealed class AgentScheduleRepository(ServiceDbContext dbContext)
    : EfRepository<AgentSchedule, Guid>(dbContext), IAgentScheduleRepository
{
    public async Task<IReadOnlyCollection<AgentSchedule>> GetAllAsync(CancellationToken cancellationToken = default)
        => await dbContext.AgentSchedules.AsNoTracking().Where(x => !x.IsDeleted).OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<AgentSchedule>> GetDueAsync(DateTime utcNow, int take, CancellationToken cancellationToken = default)
        => await dbContext.AgentSchedules
            .Where(x => !x.IsDeleted && x.IsActive && x.NextExecutionAt != null && x.NextExecutionAt <= utcNow)
            .OrderBy(x => x.NextExecutionAt)
            .Take(Math.Max(1, take))
            .ToListAsync(cancellationToken);
}

public sealed class AgentExecutionRepository(ServiceDbContext dbContext)
    : EfRepository<AgentExecution, Guid>(dbContext), IAgentExecutionRepository
{
    public async Task<IReadOnlyCollection<AgentExecution>> GetRecentAsync(int take, AgentExecutionStatus? status = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AgentExecutions.AsNoTracking().AsQueryable();
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        return await query.OrderByDescending(x => x.CreatedAtUtc).Take(Math.Max(1, take)).ToListAsync(cancellationToken);
    }
}

public sealed class AgentResultRepository(ServiceDbContext dbContext)
    : EfRepository<AgentResult, Guid>(dbContext), IAgentResultRepository
{
    public Task<AgentResult?> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default)
        => dbContext.AgentResults.AsNoTracking().FirstOrDefaultAsync(x => x.ExecutionId == executionId, cancellationToken);
}
