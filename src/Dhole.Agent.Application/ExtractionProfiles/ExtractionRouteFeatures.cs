using CustomCodeFramework.Core.Results;
using CustomCodeFramework.Cqrs.Commands;
using CustomCodeFramework.Cqrs.Queries;
using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Contracts.ExtractionProfiles;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.ExtractionProfiles;

public sealed record GetExtractionRoutesQuery(Guid ProfileId) : IQuery<IReadOnlyCollection<AgentExtractionRouteDto>>;
public sealed class GetExtractionRoutesQueryHandler(IAgentExtractionRouteRepository routes)
    : IQueryHandler<GetExtractionRoutesQuery, IReadOnlyCollection<AgentExtractionRouteDto>>
{
    public async Task<IReadOnlyCollection<AgentExtractionRouteDto>> HandleAsync(GetExtractionRoutesQuery query, CancellationToken cancellationToken = default)
        => (await routes.GetByProfileAsync(query.ProfileId, cancellationToken)).Select(Map).ToArray();

    private static AgentExtractionRouteDto Map(AgentExtractionRoute x)
        => new(x.Id, x.ProfileId, x.Name, x.PolCode, x.PolName, x.PoeCode, x.PoeName, x.PodCode, x.PodName, x.IsActive, x.SortOrder);
}

public sealed record CreateExtractionRouteCommand(Guid ProfileId, SaveAgentExtractionRouteRequest Request, Guid? ActorId) : ICommand<Result<Guid>>;
public sealed class CreateExtractionRouteCommandHandler(
    IAgentExtractionProfileRepository profiles,
    IAgentExtractionRouteRepository routes,
    IUnitOfWork unitOfWork) : ICommandHandler<CreateExtractionRouteCommand, Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(CreateExtractionRouteCommand command, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(command.ProfileId, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return Result.Failure<Guid>(new Error("Agent.ExtractionProfileNotFound", "Extraction profile not found."));

        var r = command.Request;
        var entity = AgentExtractionRoute.Create(command.ProfileId, r.Name, r.PolCode, r.PolName, r.PoeCode, r.PoeName, r.PodCode, r.PodName, r.IsActive, r.SortOrder, command.ActorId);
        await routes.AddAsync(entity, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(entity.Id);
    }
}

public sealed record UpdateExtractionRouteCommand(Guid ProfileId, Guid RouteId, SaveAgentExtractionRouteRequest Request, Guid? ActorId) : ICommand<Result>;
public sealed class UpdateExtractionRouteCommandHandler(IAgentExtractionRouteRepository routes, IUnitOfWork unitOfWork)
    : ICommandHandler<UpdateExtractionRouteCommand, Result>
{
    public async Task<Result> HandleAsync(UpdateExtractionRouteCommand command, CancellationToken cancellationToken = default)
    {
        var entity = await routes.GetByIdAsync(command.RouteId, cancellationToken);
        if (entity is null || entity.IsDeleted || entity.ProfileId != command.ProfileId)
            return Result.Failure(new Error("Agent.ExtractionRouteNotFound", "Extraction route not found."));

        var r = command.Request;
        entity.Update(r.Name, r.PolCode, r.PolName, r.PoeCode, r.PoeName, r.PodCode, r.PodName, r.IsActive, r.SortOrder, command.ActorId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record DeleteExtractionRouteCommand(Guid ProfileId, Guid RouteId, Guid? ActorId) : ICommand<Result>;
public sealed class DeleteExtractionRouteCommandHandler(IAgentExtractionRouteRepository routes, IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteExtractionRouteCommand, Result>
{
    public async Task<Result> HandleAsync(DeleteExtractionRouteCommand command, CancellationToken cancellationToken = default)
    {
        var entity = await routes.GetByIdAsync(command.RouteId, cancellationToken);
        if (entity is null || entity.IsDeleted || entity.ProfileId != command.ProfileId)
            return Result.Failure(new Error("Agent.ExtractionRouteNotFound", "Extraction route not found."));
        entity.Delete(command.ActorId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
