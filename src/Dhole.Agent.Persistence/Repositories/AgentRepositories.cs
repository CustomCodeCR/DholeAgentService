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

    public async Task<IReadOnlyCollection<AgentExecution>> GetQueuedOlderThanAsync(
        DateTime utcCutoff,
        int take,
        CancellationToken cancellationToken = default)
        => await dbContext.AgentExecutions
            .AsNoTracking()
            .Where(x => x.Status == AgentExecutionStatus.Queued && x.CreatedAtUtc <= utcCutoff
                && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= utcCutoff)
                && x.Attempt < x.MaxAttempts)
            // Explicit user-triggered executions go first, then configured priority.
            // Automated Maersk backlogs must not starve new manual work.
            .OrderByDescending(x => x.ExecutionType == AgentExecutionType.Manual)
            .ThenByDescending(x => x.Priority)
            .ThenBy(x => x.CreatedAtUtc)
            .Take(Math.Max(1, take))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<AgentExecution>> GetDispatchCandidatesAsync(
        DateTime utcCutoff, int take, CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(take, 4, 250);
        var ready = dbContext.AgentExecutions.AsNoTracking()
            .Where(x => x.Status == AgentExecutionStatus.Queued
                && x.Attempt < x.MaxAttempts
                && x.CreatedAtUtc <= utcCutoff
                && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= utcCutoff));

        // Separate independent subqueries ensure a large Maersk backlog or
        // continuous manual submissions cannot hide other providers/cron.
        var manual = await ready
            .Where(x => x.ExecutionType == AgentExecutionType.Manual)
            .OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAtUtc)
            .Take(size).ToListAsync(cancellationToken);

        var scheduled = await ready
            .Where(x => x.ExecutionType != AgentExecutionType.Manual)
            .OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAtUtc)
            .Take(size).ToListAsync(cancellationToken);

        var otherProviders = await ready
            .Where(x => dbContext.AgentProviders.Any(p =>
                p.Id == x.ProviderId && p.Code != "MAERSK"))
            .OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAtUtc)
            .Take(size).ToListAsync(cancellationToken);

        var seen = new HashSet<Guid>();
        var result = new List<AgentExecution>(size);
        var index = 0;
        while (result.Count < size && (index < manual.Count || index < scheduled.Count))
        {
            // Three user-driven executions followed by one non-manual task:
            // preference without starvation.
            for (var i = 0; i < 3 && result.Count < size && index * 3 + i < manual.Count; i++)
            {
                var item = manual[index * 3 + i];
                if (seen.Add(item.Id)) result.Add(item);
            }
            if (index < scheduled.Count && result.Count < size)
            {
                var item = scheduled[index];
                if (seen.Add(item.Id)) result.Add(item);
            }
            index++;
        }

        // Reserve visibility for providers unrelated to Maersk even when all
        // first-page candidates happen to use a blocked Maersk profile.
        foreach (var item in otherProviders)
        {
            if (seen.Contains(item.Id)) continue;
            if (result.Count >= size)
            {
                var replaceIndex = result.FindLastIndex(x => !otherProviders.Any(o => o.Id == x.Id));
                if (replaceIndex < 0) break;
                seen.Remove(result[replaceIndex].Id);
                result.RemoveAt(replaceIndex);
            }
            if (seen.Add(item.Id)) result.Add(item);
            if (result.Count >= size) break;
        }
        return result;
    }

    public async Task<IReadOnlyCollection<AgentExecution>> GetQueuedByProviderAsync(
        Guid providerId,
        int take,
        CancellationToken cancellationToken = default)
        => await dbContext.AgentExecutions
            .Where(x => x.ProviderId == providerId && x.Status == AgentExecutionStatus.Queued)
            .OrderBy(x => x.CreatedAtUtc)
            .Take(Math.Clamp(take, 1, 2000))
            .ToListAsync(cancellationToken);

    public Task<bool> HasOutstandingForScheduleAsync(
        Guid scheduleId,
        CancellationToken cancellationToken = default)
        => dbContext.AgentExecutions.AnyAsync(
            x => x.ScheduleId == scheduleId &&
                (x.Status == AgentExecutionStatus.Queued ||
                 x.Status == AgentExecutionStatus.Running),
            cancellationToken);
}

public sealed class AgentResultRepository(ServiceDbContext dbContext)
    : EfRepository<AgentResult, Guid>(dbContext), IAgentResultRepository
{
    public Task<AgentResult?> GetByExecutionIdAsync(Guid executionId, CancellationToken cancellationToken = default)
        => dbContext.AgentResults.AsNoTracking().FirstOrDefaultAsync(x => x.ExecutionId == executionId, cancellationToken);
}
