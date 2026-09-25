using CustomCodeFramework.Core.Results;
using CustomCodeFramework.Cqrs.Commands;
using CustomCodeFramework.Cqrs.Queries;
using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Application.Agents;
using Dhole.Agent.Contracts.ExtractionProfiles;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.ExtractionProfiles;

public sealed record GetAgentExtractionProfilesQuery : IQuery<IReadOnlyCollection<AgentExtractionProfileDto>>;

public sealed class GetAgentExtractionProfilesQueryHandler(IAgentExtractionProfileRepository profiles)
    : IQueryHandler<GetAgentExtractionProfilesQuery, IReadOnlyCollection<AgentExtractionProfileDto>>
{
    public async Task<IReadOnlyCollection<AgentExtractionProfileDto>> HandleAsync(
        GetAgentExtractionProfilesQuery query,
        CancellationToken cancellationToken = default)
        => (await profiles.GetAllAsync(cancellationToken)).Select(ToDto).ToArray();

    private static AgentExtractionProfileDto ToDto(AgentExtractionProfile x)
        => new(
            x.Id,
            x.ProviderId,
            x.CredentialId,
            x.Name,
            x.Description,
            x.BaseUrl,
            x.LoginUrl,
            x.SearchUrl,
            x.PromptTemplate,
            x.ExecutionStrategy.ToString(),
            x.ParserKey,
            x.IsActive,
            x.CreatedAtUtc,
            x.UpdatedAtUtc);
}

public sealed record GetAgentExtractionProfileByIdQuery(Guid Id) : IQuery<Result<AgentExtractionProfileDto>>;

public sealed class GetAgentExtractionProfileByIdQueryHandler(IAgentExtractionProfileRepository profiles)
    : IQueryHandler<GetAgentExtractionProfileByIdQuery, Result<AgentExtractionProfileDto>>
{
    public async Task<Result<AgentExtractionProfileDto>> HandleAsync(
        GetAgentExtractionProfileByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(query.Id, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return Result.Failure<AgentExtractionProfileDto>(AgentErrors.ExtractionProfileNotFound);

        return Result.Success(new AgentExtractionProfileDto(
            profile.Id,
            profile.ProviderId,
            profile.CredentialId,
            profile.Name,
            profile.Description,
            profile.BaseUrl,
            profile.LoginUrl,
            profile.SearchUrl,
            profile.PromptTemplate,
            profile.ExecutionStrategy.ToString(),
            profile.ParserKey,
            profile.IsActive,
            profile.CreatedAtUtc,
            profile.UpdatedAtUtc));
    }
}

public sealed record CreateAgentExtractionProfileCommand(
    Guid ProviderId,
    Guid? CredentialId,
    string Name,
    string? Description,
    string? BaseUrl,
    string? LoginUrl,
    string? SearchUrl,
    string PromptTemplate,
    AgentExecutionStrategy ExecutionStrategy,
    string? ParserKey,
    Guid? ActorId) : ICommand<Result<Guid>>;

public sealed class CreateAgentExtractionProfileCommandHandler(
    IAgentExtractionProfileRepository profiles,
    IAgentProviderRepository providers,
    IAgentCredentialRepository credentials,
    IUnitOfWork unitOfWork)
    : ICommandHandler<CreateAgentExtractionProfileCommand, Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(
        CreateAgentExtractionProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        var provider = await providers.GetByIdAsync(command.ProviderId, cancellationToken);
        if (provider is null || provider.IsDeleted)
            return Result.Failure<Guid>(AgentErrors.ProviderNotFound);

        if (command.CredentialId.HasValue)
        {
            var credential = await credentials.GetByIdAsync(command.CredentialId.Value, cancellationToken);
            if (credential is null || credential.IsDeleted)
                return Result.Failure<Guid>(AgentErrors.CredentialNotFound);
            if (credential.ProviderId != command.ProviderId)
                return Result.Failure<Guid>(AgentErrors.ExtractionProfileCredentialProviderMismatch);
        }

        var profile = AgentExtractionProfile.Create(
            command.ProviderId,
            command.CredentialId,
            command.Name,
            command.Description,
            command.BaseUrl,
            command.LoginUrl,
            command.SearchUrl,
            command.PromptTemplate,
            command.ExecutionStrategy,
            command.ParserKey,
            command.ActorId);

        await profiles.AddAsync(profile, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(profile.Id);
    }
}

public sealed record UpdateAgentExtractionProfileCommand(
    Guid Id,
    Guid? CredentialId,
    string Name,
    string? Description,
    string? BaseUrl,
    string? LoginUrl,
    string? SearchUrl,
    string PromptTemplate,
    AgentExecutionStrategy ExecutionStrategy,
    string? ParserKey,
    Guid? ActorId) : ICommand<Result>;

public sealed class UpdateAgentExtractionProfileCommandHandler(
    IAgentExtractionProfileRepository profiles,
    IAgentCredentialRepository credentials,
    IUnitOfWork unitOfWork)
    : ICommandHandler<UpdateAgentExtractionProfileCommand, Result>
{
    public async Task<Result> HandleAsync(
        UpdateAgentExtractionProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(command.Id, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return Result.Failure(AgentErrors.ExtractionProfileNotFound);

        if (command.CredentialId.HasValue)
        {
            var credential = await credentials.GetByIdAsync(command.CredentialId.Value, cancellationToken);
            if (credential is null || credential.IsDeleted)
                return Result.Failure(AgentErrors.CredentialNotFound);
            if (credential.ProviderId != profile.ProviderId)
                return Result.Failure(AgentErrors.ExtractionProfileCredentialProviderMismatch);
        }

        profile.Update(
            command.CredentialId,
            command.Name,
            command.Description,
            command.BaseUrl,
            command.LoginUrl,
            command.SearchUrl,
            command.PromptTemplate,
            command.ExecutionStrategy,
            command.ParserKey,
            command.ActorId);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record SetAgentExtractionProfileActiveCommand(Guid Id, bool IsActive, Guid? ActorId)
    : ICommand<Result>;

public sealed class SetAgentExtractionProfileActiveCommandHandler(
    IAgentExtractionProfileRepository profiles,
    IUnitOfWork unitOfWork)
    : ICommandHandler<SetAgentExtractionProfileActiveCommand, Result>
{
    public async Task<Result> HandleAsync(
        SetAgentExtractionProfileActiveCommand command,
        CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(command.Id, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return Result.Failure(AgentErrors.ExtractionProfileNotFound);

        profile.SetActive(command.IsActive, command.ActorId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record DeleteAgentExtractionProfileCommand(Guid Id, Guid? ActorId) : ICommand<Result>;

public sealed class DeleteAgentExtractionProfileCommandHandler(
    IAgentExtractionProfileRepository profiles,
    IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteAgentExtractionProfileCommand, Result>
{
    public async Task<Result> HandleAsync(
        DeleteAgentExtractionProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(command.Id, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return Result.Failure(AgentErrors.ExtractionProfileNotFound);

        profile.Delete(command.ActorId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
