using System.Text.Json;
using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Application.ExtractionProfiles;
using Dhole.Agent.Domain.Agents;
using Microsoft.Extensions.Logging;

namespace Dhole.Agent.Application.Runtime;

public sealed class AgentExecutionOrchestrator(
    IAgentExecutionRepository executions,
    IAgentDefinitionRepository definitions,
    IAgentProviderRepository providers,
    IAgentCredentialRepository credentials,
    IAgentExtractionProfileRepository extractionProfiles,
    IAgentExtractionRouteRepository extractionRoutes,
    IAgentExtractionEquipmentRepository extractionEquipment,
    IAgentExtractionFieldRepository extractionFields,
    IAgentEndpointCaptureRepository endpointCaptures,
    AgentExecutionSnapshotBuilder snapshotBuilder,
    IAgentResultRepository results,
    IAgentProviderResolver providerResolver,
    IUnitOfWork unitOfWork,
    ILogger<AgentExecutionOrchestrator> logger):IAgentExecutionOrchestrator
{
    public async Task ExecuteAsync(Guid executionId,CancellationToken cancellationToken=default)
    {
        var execution=await executions.GetByIdAsync(executionId,cancellationToken);
        if(execution is null)
        {
            logger.LogError("AGENT_EXECUTION_NOT_FOUND execution={ExecutionId}", executionId);
            return;
        }

        if(execution.Status is AgentExecutionStatus.Completed or AgentExecutionStatus.Cancelled or AgentExecutionStatus.Failed)
        {
            logger.LogInformation(
                "AGENT_EXECUTION_SKIPPED execution={ExecutionId} status={Status}",
                execution.Id,
                execution.Status);
            return;
        }

        if(execution.Status == AgentExecutionStatus.Running)
        {
            logger.LogInformation(
                "AGENT_EXECUTION_ALREADY_RUNNING execution={ExecutionId}",
                execution.Id);
            return;
        }

        try
        {
            var definition=await definitions.GetByIdAsync(execution.AgentDefinitionId,cancellationToken)
                ?? throw new InvalidOperationException($"Agent definition {execution.AgentDefinitionId} not found.");
            var provider=await providers.GetByIdAsync(execution.ProviderId,cancellationToken)
                ?? throw new InvalidOperationException($"Agent provider {execution.ProviderId} not found.");

            var profile=await ResolveExecutionProfileAsync(execution,cancellationToken);
            if(profile is null)
            {
                execution.Fail(
                    "extraction_profile_not_found",
                    $"No active extraction profile was found for provider '{provider.Code}' and credential '{execution.CredentialId}'.",
                    DateTime.UtcNow);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }

            if(execution.ExtractionProfileId!=profile.Id
               ||string.IsNullOrWhiteSpace(execution.PromptSnapshot)
               ||string.IsNullOrWhiteSpace(execution.ConfigurationSnapshotJson))
            {
                var routes=await extractionRoutes.GetByProfileAsync(profile.Id,cancellationToken);
                var equipment=await extractionEquipment.GetByProfileAsync(profile.Id,cancellationToken);
                var fields=await extractionFields.GetByProfileAsync(profile.Id,cancellationToken);
                var captures=await endpointCaptures.GetByProfileAsync(profile.Id,cancellationToken);

                var snapshot=snapshotBuilder.Build(
                    profile,
                    provider,
                    routes,
                    equipment,
                    fields,
                    captures,
                    TryGetCargoReadyDate(execution.InputJson),
                    execution.Id);

                execution.AttachProfileSnapshot(
                    profile.Id,
                    snapshot.Prompt,
                    snapshot.ConfigurationJson);

                await unitOfWork.SaveChangesAsync(cancellationToken);

                logger.LogInformation(
                    "AGENT_EXECUTION_PROFILE_ATTACHED execution={ExecutionId} profile={ProfileId} strategy={Strategy}",
                    execution.Id,
                    profile.Id,
                    profile.ExecutionStrategy);
            }

            AgentCredential? credential=null;
            var credentialId=profile.CredentialId??execution.CredentialId;
            if(credentialId.HasValue)
            {
                credential=await credentials.GetByIdAsync(credentialId.Value,cancellationToken);
                if(credential is null||credential.IsDeleted||!credential.IsActive)
                {
                    execution.Fail(
                        "extraction_profile_credential_not_found",
                        $"Extraction profile '{profile.Name}' references an unavailable credential '{credentialId.Value}'.",
                        DateTime.UtcNow);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    return;
                }
            }

            logger.LogWarning(
                "AGENT_EXECUTION_STARTING execution={ExecutionId} status={Status} attempt={Attempt} profile={ProfileId} strategy={Strategy}",
                execution.Id,
                execution.Status,
                execution.Attempt,
                profile.Id,
                profile.ExecutionStrategy);

            execution.Start(DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogWarning(
                "AGENT_EXECUTION_RUNNING execution={ExecutionId} attempt={Attempt} started={StartedAt} profile={ProfileId} strategy={Strategy}",
                execution.Id,
                execution.Attempt,
                execution.StartedAt,
                profile.Id,
                profile.ExecutionStrategy);

            var runner=providerResolver.Resolve(provider.Code,profile.ExecutionStrategy);
            var result=await runner.ExecuteAsync(
                new AgentExecutionContext(execution,definition,provider,credential),
                cancellationToken);

            if(!result.Success)
            {
                execution.Fail(
                    result.ErrorCode??"provider_failed",
                    result.ErrorMessage??"Provider execution failed.",
                    DateTime.UtcNow);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }

            if(!string.IsNullOrWhiteSpace(result.ResultType)&&!string.IsNullOrWhiteSpace(result.DataJson))
            {
                var existing=await results.GetByExecutionIdAsync(execution.Id,cancellationToken);
                if(existing is null)
                    await results.AddAsync(
                        AgentResult.Create(
                            execution.Id,
                            provider.Id,
                            result.ResultType,
                            result.SchemaVersion??"1.0",
                            result.DataJson),
                        cancellationToken);
            }

            execution.Complete(result.OutputJson,DateTime.UtcNow,result.PartiallyCompleted);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch(Exception ex) when(execution.Status is not AgentExecutionStatus.Completed and not AgentExecutionStatus.Cancelled)
        {
            logger.LogError(
                ex,
                "AGENT_EXECUTION_ERROR execution={ExecutionId} status={Status}",
                execution.Id,
                execution.Status);

            execution.Fail("agent_runtime_error",ex.Message,DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<AgentExtractionProfile?> ResolveExecutionProfileAsync(
        AgentExecution execution,
        CancellationToken cancellationToken)
    {
        if(execution.ExtractionProfileId.HasValue)
        {
            var attached=await extractionProfiles.GetByIdAsync(execution.ExtractionProfileId.Value,cancellationToken);
            if(attached is not null&&!attached.IsDeleted&&attached.IsActive)
                return attached;
        }

        var candidates=(await extractionProfiles.GetAllAsync(cancellationToken))
            .Where(x=>x.IsActive&&!x.IsDeleted&&x.ProviderId==execution.ProviderId)
            .ToArray();

        if(candidates.Length==0)return null;

        if(execution.CredentialId.HasValue)
        {
            var exact=candidates
                .Where(x=>x.CredentialId==execution.CredentialId)
                .OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase)
                .ThenBy(x=>x.Id)
                .ToArray();

            if(exact.Length>0)
            {
                if(exact.Length>1)
                    logger.LogWarning(
                        "AGENT_EXECUTION_MULTIPLE_PROFILES execution={ExecutionId} provider={ProviderId} credential={CredentialId} selected={ProfileId} count={Count}",
                        execution.Id,
                        execution.ProviderId,
                        execution.CredentialId,
                        exact[0].Id,
                        exact.Length);

                return exact[0];
            }
        }

        var withoutCredential=candidates
            .Where(x=>!x.CredentialId.HasValue)
            .OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase)
            .ThenBy(x=>x.Id)
            .ToArray();

        if(withoutCredential.Length>0)return withoutCredential[0];

        return candidates
            .OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase)
            .ThenBy(x=>x.Id)
            .First();
    }

    private static DateOnly? TryGetCargoReadyDate(string inputJson)
    {
        try
        {
            using var document=JsonDocument.Parse(inputJson);
            if(!document.RootElement.TryGetProperty("cargoReadyDate",out var value)
               ||value.ValueKind!=JsonValueKind.String)
                return null;

            return DateOnly.TryParse(value.GetString(),out var date)?date:null;
        }
        catch(JsonException)
        {
            return null;
        }
    }
}
