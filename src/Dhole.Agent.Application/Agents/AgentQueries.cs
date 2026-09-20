using CustomCodeFramework.Core.Results;
using CustomCodeFramework.Cqrs.Queries;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Contracts.Agents;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.Agents;

public sealed record GetAgentProvidersQuery:IQuery<IReadOnlyCollection<AgentProviderDto>>;
public sealed class GetAgentProvidersQueryHandler(IAgentProviderRepository repo):IQueryHandler<GetAgentProvidersQuery,IReadOnlyCollection<AgentProviderDto>>
{public async Task<IReadOnlyCollection<AgentProviderDto>> HandleAsync(GetAgentProvidersQuery q,CancellationToken ct=default)=>(await repo.GetAllAsync(ct)).Select(x=>x.ToDto()).ToArray();}
public sealed record GetAgentProviderByIdQuery(Guid Id):IQuery<Result<AgentProviderDto>>;
public sealed class GetAgentProviderByIdQueryHandler(IAgentProviderRepository repo):IQueryHandler<GetAgentProviderByIdQuery,Result<AgentProviderDto>>
{public async Task<Result<AgentProviderDto>> HandleAsync(GetAgentProviderByIdQuery q,CancellationToken ct=default){var e=await repo.GetByIdAsync(q.Id,ct);return e is null||e.IsDeleted?Result.Failure<AgentProviderDto>(AgentErrors.ProviderNotFound):Result.Success(e.ToDto());}}

public sealed record GetAgentDefinitionsQuery(Guid? ProviderId):IQuery<IReadOnlyCollection<AgentDefinitionDto>>;
public sealed class GetAgentDefinitionsQueryHandler(IAgentDefinitionRepository repo):IQueryHandler<GetAgentDefinitionsQuery,IReadOnlyCollection<AgentDefinitionDto>>
{public async Task<IReadOnlyCollection<AgentDefinitionDto>> HandleAsync(GetAgentDefinitionsQuery q,CancellationToken ct=default)=>(await repo.GetAllAsync(q.ProviderId,ct)).Select(x=>x.ToDto()).ToArray();}
public sealed record GetAgentDefinitionByIdQuery(Guid Id):IQuery<Result<AgentDefinitionDto>>;
public sealed class GetAgentDefinitionByIdQueryHandler(IAgentDefinitionRepository repo):IQueryHandler<GetAgentDefinitionByIdQuery,Result<AgentDefinitionDto>>
{public async Task<Result<AgentDefinitionDto>> HandleAsync(GetAgentDefinitionByIdQuery q,CancellationToken ct=default){var e=await repo.GetByIdAsync(q.Id,ct);return e is null||e.IsDeleted?Result.Failure<AgentDefinitionDto>(AgentErrors.DefinitionNotFound):Result.Success(e.ToDto());}}

public sealed record GetAgentCredentialsQuery(Guid? ProviderId):IQuery<IReadOnlyCollection<AgentCredentialDto>>;
public sealed class GetAgentCredentialsQueryHandler(IAgentCredentialRepository repo):IQueryHandler<GetAgentCredentialsQuery,IReadOnlyCollection<AgentCredentialDto>>
{public async Task<IReadOnlyCollection<AgentCredentialDto>> HandleAsync(GetAgentCredentialsQuery q,CancellationToken ct=default)=>(await repo.GetAllAsync(q.ProviderId,ct)).Select(x=>x.ToDto()).ToArray();}

public sealed record GetBrowserProfilesQuery(Guid? ProviderId):IQuery<IReadOnlyCollection<BrowserProfileDto>>;
public sealed class GetBrowserProfilesQueryHandler(IBrowserProfileRepository repo):IQueryHandler<GetBrowserProfilesQuery,IReadOnlyCollection<BrowserProfileDto>>
{public async Task<IReadOnlyCollection<BrowserProfileDto>> HandleAsync(GetBrowserProfilesQuery q,CancellationToken ct=default)=>(await repo.GetAllAsync(q.ProviderId,ct)).Select(x=>x.ToDto()).ToArray();}
public sealed record GetBrowserProfileByIdQuery(Guid Id):IQuery<Result<BrowserProfileDto>>;
public sealed class GetBrowserProfileByIdQueryHandler(IBrowserProfileRepository repo):IQueryHandler<GetBrowserProfileByIdQuery,Result<BrowserProfileDto>>
{public async Task<Result<BrowserProfileDto>> HandleAsync(GetBrowserProfileByIdQuery q,CancellationToken ct=default){var e=await repo.GetByIdAsync(q.Id,ct);return e is null||e.IsDeleted?Result.Failure<BrowserProfileDto>(AgentErrors.BrowserProfileNotFound):Result.Success(e.ToDto());}}

public sealed record GetAgentSchedulesQuery:IQuery<IReadOnlyCollection<AgentScheduleDto>>;
public sealed class GetAgentSchedulesQueryHandler(IAgentScheduleRepository repo):IQueryHandler<GetAgentSchedulesQuery,IReadOnlyCollection<AgentScheduleDto>>
{public async Task<IReadOnlyCollection<AgentScheduleDto>> HandleAsync(GetAgentSchedulesQuery q,CancellationToken ct=default)=>(await repo.GetAllAsync(ct)).Select(x=>x.ToDto()).ToArray();}
public sealed record GetAgentScheduleByIdQuery(Guid Id):IQuery<Result<AgentScheduleDto>>;
public sealed class GetAgentScheduleByIdQueryHandler(IAgentScheduleRepository repo):IQueryHandler<GetAgentScheduleByIdQuery,Result<AgentScheduleDto>>
{public async Task<Result<AgentScheduleDto>> HandleAsync(GetAgentScheduleByIdQuery q,CancellationToken ct=default){var e=await repo.GetByIdAsync(q.Id,ct);return e is null||e.IsDeleted?Result.Failure<AgentScheduleDto>(AgentErrors.ScheduleNotFound):Result.Success(e.ToDto());}}

public sealed record GetAgentExecutionsQuery(int Take,AgentExecutionStatus? Status):IQuery<IReadOnlyCollection<AgentExecutionDto>>;
public sealed class GetAgentExecutionsQueryHandler(IAgentExecutionRepository repo):IQueryHandler<GetAgentExecutionsQuery,IReadOnlyCollection<AgentExecutionDto>>
{public async Task<IReadOnlyCollection<AgentExecutionDto>> HandleAsync(GetAgentExecutionsQuery q,CancellationToken ct=default)=>(await repo.GetRecentAsync(Math.Clamp(q.Take,1,200),q.Status,ct)).Select(x=>x.ToDto()).ToArray();}
public sealed record GetAgentExecutionByIdQuery(Guid Id):IQuery<Result<AgentExecutionDto>>;
public sealed class GetAgentExecutionByIdQueryHandler(IAgentExecutionRepository repo):IQueryHandler<GetAgentExecutionByIdQuery,Result<AgentExecutionDto>>
{public async Task<Result<AgentExecutionDto>> HandleAsync(GetAgentExecutionByIdQuery q,CancellationToken ct=default){var e=await repo.GetByIdAsync(q.Id,ct);return e is null?Result.Failure<AgentExecutionDto>(AgentErrors.ExecutionNotFound):Result.Success(e.ToDto());}}

public sealed record GetAgentExecutionResultQuery(Guid ExecutionId):IQuery<Result<AgentResultDto>>;
public sealed class GetAgentExecutionResultQueryHandler(IAgentResultRepository repo):IQueryHandler<GetAgentExecutionResultQuery,Result<AgentResultDto>>
{
    public async Task<Result<AgentResultDto>> HandleAsync(GetAgentExecutionResultQuery q,CancellationToken ct=default)
    {
        var e=await repo.GetByExecutionIdAsync(q.ExecutionId,ct);
        return e is null
            ? Result.Failure<AgentResultDto>(AgentErrors.ExecutionResultNotFound)
            : Result.Success(e.ToDto());
    }
}
