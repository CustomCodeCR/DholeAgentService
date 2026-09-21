using CustomCodeFramework.Core.Results;
using CustomCodeFramework.Cqrs.Commands;
using CustomCodeFramework.Cqrs.Queries;
using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Contracts.ExtractionProfiles;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.ExtractionProfiles;

public sealed record GetExtractionEquipmentQuery(Guid ProfileId) : IQuery<IReadOnlyCollection<AgentExtractionEquipmentDto>>;
public sealed class GetExtractionEquipmentQueryHandler(IAgentExtractionEquipmentRepository equipment)
    : IQueryHandler<GetExtractionEquipmentQuery, IReadOnlyCollection<AgentExtractionEquipmentDto>>
{
    public async Task<IReadOnlyCollection<AgentExtractionEquipmentDto>> HandleAsync(GetExtractionEquipmentQuery query, CancellationToken cancellationToken = default)
        => (await equipment.GetByProfileAsync(query.ProfileId, cancellationToken)).Select(Map).ToArray();

    private static AgentExtractionEquipmentDto Map(AgentExtractionEquipment x)
        => new(x.Id, x.ProfileId, x.Code, x.Name, x.Quantity, x.DefaultWeightKg, x.IsActive, x.SortOrder);
}

public sealed record CreateExtractionEquipmentCommand(Guid ProfileId, SaveAgentExtractionEquipmentRequest Request, Guid? ActorId) : ICommand<Result<Guid>>;
public sealed class CreateExtractionEquipmentCommandHandler(IAgentExtractionProfileRepository profiles, IAgentExtractionEquipmentRepository equipment, IUnitOfWork unitOfWork)
    : ICommandHandler<CreateExtractionEquipmentCommand, Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(CreateExtractionEquipmentCommand command, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(command.ProfileId, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return Result.Failure<Guid>(new Error("Agent.ExtractionProfileNotFound", "Extraction profile not found."));

        var r = command.Request;
        var entity = AgentExtractionEquipment.Create(command.ProfileId, r.Code, r.Name, r.Quantity, r.DefaultWeightKg, r.IsActive, r.SortOrder, command.ActorId);
        await equipment.AddAsync(entity, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(entity.Id);
    }
}

public sealed record UpdateExtractionEquipmentCommand(Guid ProfileId, Guid EquipmentId, SaveAgentExtractionEquipmentRequest Request, Guid? ActorId) : ICommand<Result>;
public sealed class UpdateExtractionEquipmentCommandHandler(IAgentExtractionEquipmentRepository equipment, IUnitOfWork unitOfWork)
    : ICommandHandler<UpdateExtractionEquipmentCommand, Result>
{
    public async Task<Result> HandleAsync(UpdateExtractionEquipmentCommand command, CancellationToken cancellationToken = default)
    {
        var entity = await equipment.GetByIdAsync(command.EquipmentId, cancellationToken);
        if (entity is null || entity.IsDeleted || entity.ProfileId != command.ProfileId)
            return Result.Failure(new Error("Agent.ExtractionEquipmentNotFound", "Extraction equipment not found."));
        var r = command.Request;
        entity.Update(r.Code, r.Name, r.Quantity, r.DefaultWeightKg, r.IsActive, r.SortOrder, command.ActorId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record DeleteExtractionEquipmentCommand(Guid ProfileId, Guid EquipmentId, Guid? ActorId) : ICommand<Result>;
public sealed class DeleteExtractionEquipmentCommandHandler(IAgentExtractionEquipmentRepository equipment, IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteExtractionEquipmentCommand, Result>
{
    public async Task<Result> HandleAsync(DeleteExtractionEquipmentCommand command, CancellationToken cancellationToken = default)
    {
        var entity = await equipment.GetByIdAsync(command.EquipmentId, cancellationToken);
        if (entity is null || entity.IsDeleted || entity.ProfileId != command.ProfileId)
            return Result.Failure(new Error("Agent.ExtractionEquipmentNotFound", "Extraction equipment not found."));
        entity.Delete(command.ActorId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
