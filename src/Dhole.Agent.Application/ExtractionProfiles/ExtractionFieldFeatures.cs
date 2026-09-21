using CustomCodeFramework.Core.Results;
using CustomCodeFramework.Cqrs.Commands;
using CustomCodeFramework.Cqrs.Queries;
using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Contracts.ExtractionProfiles;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.ExtractionProfiles;

public sealed record GetExtractionFieldsQuery(Guid ProfileId) : IQuery<IReadOnlyCollection<AgentExtractionFieldDto>>;
public sealed class GetExtractionFieldsQueryHandler(IAgentExtractionFieldRepository fields)
    : IQueryHandler<GetExtractionFieldsQuery, IReadOnlyCollection<AgentExtractionFieldDto>>
{
    public async Task<IReadOnlyCollection<AgentExtractionFieldDto>> HandleAsync(GetExtractionFieldsQuery query, CancellationToken cancellationToken = default)
        => (await fields.GetByProfileAsync(query.ProfileId, cancellationToken)).Select(Map).ToArray();

    private static AgentExtractionFieldDto Map(AgentExtractionField x)
        => new(x.Id, x.ProfileId, x.Key, x.Label, x.Description, x.DataType.ToString(), x.SourceType.ToString(), x.JsonPath, x.Required, x.SortOrder, x.IsActive);
}

public sealed record CreateExtractionFieldCommand(Guid ProfileId, SaveAgentExtractionFieldRequest Request, Guid? ActorId) : ICommand<Result<Guid>>;
public sealed class CreateExtractionFieldCommandHandler(IAgentExtractionProfileRepository profiles, IAgentExtractionFieldRepository fields, IUnitOfWork unitOfWork)
    : ICommandHandler<CreateExtractionFieldCommand, Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(CreateExtractionFieldCommand command, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(command.ProfileId, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return Result.Failure<Guid>(new Error("Agent.ExtractionProfileNotFound", "Extraction profile not found."));
        if (!TryParse(command.Request, out var dataType, out var sourceType, out var error))
            return Result.Failure<Guid>(error!);

        var r = command.Request;
        var entity = AgentExtractionField.Create(command.ProfileId, r.Key, r.Label, r.Description, dataType, sourceType, r.JsonPath, r.Required, r.SortOrder, r.IsActive, command.ActorId);
        await fields.AddAsync(entity, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(entity.Id);
    }

    internal static bool TryParse(SaveAgentExtractionFieldRequest request, out AgentExtractionDataType dataType, out AgentExtractionSourceType sourceType, out Error? error)
    {
        if (!Enum.TryParse(request.DataType, true, out dataType))
        {
            sourceType = default;
            error = new Error("Agent.InvalidExtractionDataType", "Extraction data type is invalid.");
            return false;
        }
        if (!Enum.TryParse(request.SourceType, true, out sourceType))
        {
            error = new Error("Agent.InvalidExtractionSourceType", "Extraction source type is invalid.");
            return false;
        }
        error = null;
        return true;
    }
}

public sealed record UpdateExtractionFieldCommand(Guid ProfileId, Guid FieldId, SaveAgentExtractionFieldRequest Request, Guid? ActorId) : ICommand<Result>;
public sealed class UpdateExtractionFieldCommandHandler(IAgentExtractionFieldRepository fields, IUnitOfWork unitOfWork)
    : ICommandHandler<UpdateExtractionFieldCommand, Result>
{
    public async Task<Result> HandleAsync(UpdateExtractionFieldCommand command, CancellationToken cancellationToken = default)
    {
        var entity = await fields.GetByIdAsync(command.FieldId, cancellationToken);
        if (entity is null || entity.IsDeleted || entity.ProfileId != command.ProfileId)
            return Result.Failure(new Error("Agent.ExtractionFieldNotFound", "Extraction field not found."));
        if (!CreateExtractionFieldCommandHandler.TryParse(command.Request, out var dataType, out var sourceType, out var error))
            return Result.Failure(error!);
        var r = command.Request;
        entity.Update(r.Key, r.Label, r.Description, dataType, sourceType, r.JsonPath, r.Required, r.SortOrder, r.IsActive, command.ActorId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record DeleteExtractionFieldCommand(Guid ProfileId, Guid FieldId, Guid? ActorId) : ICommand<Result>;
public sealed class DeleteExtractionFieldCommandHandler(IAgentExtractionFieldRepository fields, IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteExtractionFieldCommand, Result>
{
    public async Task<Result> HandleAsync(DeleteExtractionFieldCommand command, CancellationToken cancellationToken = default)
    {
        var entity = await fields.GetByIdAsync(command.FieldId, cancellationToken);
        if (entity is null || entity.IsDeleted || entity.ProfileId != command.ProfileId)
            return Result.Failure(new Error("Agent.ExtractionFieldNotFound", "Extraction field not found."));
        entity.Delete(command.ActorId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
