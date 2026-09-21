using CustomCodeFramework.Core.Results;
using CustomCodeFramework.Cqrs.Queries;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Contracts.ExtractionProfiles;

namespace Dhole.Agent.Application.ExtractionProfiles;

public sealed record GetAgentPromptPreviewQuery(Guid ProfileId, AgentPromptPreviewRequest Request) : IQuery<Result<AgentPromptPreviewDto>>;

public sealed class GetAgentPromptPreviewQueryHandler(
    IAgentExtractionProfileRepository profiles,
    IAgentProviderRepository providers,
    IAgentExtractionRouteRepository routes,
    IAgentExtractionEquipmentRepository equipment,
    IAgentExtractionFieldRepository fields,
    IAgentEndpointCaptureRepository captures,
    AgentPromptBuilder builder) : IQueryHandler<GetAgentPromptPreviewQuery, Result<AgentPromptPreviewDto>>
{
    public async Task<Result<AgentPromptPreviewDto>> HandleAsync(GetAgentPromptPreviewQuery query, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(query.ProfileId, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return Result.Failure<AgentPromptPreviewDto>(new Error("Agent.ExtractionProfileNotFound", "Extraction profile not found."));

        var provider = await providers.GetByIdAsync(profile.ProviderId, cancellationToken);
        if (provider is null || provider.IsDeleted)
            return Result.Failure<AgentPromptPreviewDto>(new Error("Agent.ProviderNotFound", "Agent provider not found."));

        var routeItems = await routes.GetByProfileAsync(profile.Id, cancellationToken);
        var equipmentItems = await equipment.GetByProfileAsync(profile.Id, cancellationToken);
        var fieldItems = await fields.GetByProfileAsync(profile.Id, cancellationToken);
        var captureItems = await captures.GetByProfileAsync(profile.Id, cancellationToken);

        var prompt = builder.Build(
            profile,
            provider,
            routeItems,
            equipmentItems,
            fieldItems,
            captureItems,
            query.Request.CargoReadyDate,
            query.Request.ExecutionId);

        return Result.Success(new AgentPromptPreviewDto(prompt, builder.AvailableVariables));
    }
}
