using CustomCodeFramework.Api.Responses;
using CustomCodeFramework.Cqrs.Commands;
using CustomCodeFramework.Cqrs.Dispatching;
using CustomCodeFramework.Cqrs.Queries;
using Dhole.Agent.Api.Authorization;
using Dhole.Agent.Api.Extensions;
using Dhole.Agent.Application.Agents;
using Dhole.Agent.Application.ExtractionProfiles;
using Dhole.Agent.Contracts.Agents;
using Dhole.Agent.Contracts.ExtractionProfiles;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Api.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var root=app.MapGroup("/api/agents").WithTags("Agents").RequireAuthorization();

        var providers=root.MapGroup("/providers");
        providers.MapGet("/",async(IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentProviderDto>>.Ok(await d.DispatchAsync(new GetAgentProvidersQuery(),ct)))).RequireScope(AgentScopeNames.ProvidersView);
        providers.MapGet("/{id:guid}",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetAgentProviderByIdQuery(id),ct),h)).RequireScope(AgentScopeNames.ProvidersView);
        providers.MapPost("/",async(CreateAgentProviderRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateAgentProviderCommand(r.Code,r.Name,ParseEnum<AgentProviderType>(r.ProviderType),r.BaseUrl,ParseEnum<AgentExecutionStrategy>(r.DefaultExecutionStrategy),r.IsSystem,r.MetadataJson,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.ProvidersManage);
        providers.MapPut("/{id:guid}",async(Guid id,UpdateAgentProviderRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateAgentProviderCommand(id,r.Name,ParseEnum<AgentProviderType>(r.ProviderType),r.BaseUrl,ParseEnum<AgentExecutionStrategy>(r.DefaultExecutionStrategy),r.MetadataJson,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.ProvidersManage);
        providers.MapPatch("/{id:guid}/active",async(Guid id,SetActiveRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new SetAgentProviderActiveCommand(id,r.IsActive,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.ProvidersManage);

        var defs=root.MapGroup("/definitions");
        defs.MapGet("/",async(Guid? providerId,IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentDefinitionDto>>.Ok(await d.DispatchAsync(new GetAgentDefinitionsQuery(providerId),ct)))).RequireScope(AgentScopeNames.DefinitionsView);
        defs.MapGet("/{id:guid}",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetAgentDefinitionByIdQuery(id),ct),h)).RequireScope(AgentScopeNames.DefinitionsView);
        defs.MapPost("/",async(CreateAgentDefinitionRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateAgentDefinitionCommand(r.ProviderId,r.Code,r.Name,r.Description,ParseEnum<AgentActionType>(r.ActionType),ParseEnum<AgentExecutionStrategy>(r.ExecutionStrategy),r.ConfigurationJson,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.DefinitionsManage);
        defs.MapPut("/{id:guid}",async(Guid id,UpdateAgentDefinitionRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateAgentDefinitionCommand(id,r.Name,r.Description,ParseEnum<AgentActionType>(r.ActionType),ParseEnum<AgentExecutionStrategy>(r.ExecutionStrategy),r.ConfigurationJson,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.DefinitionsManage);
        defs.MapPatch("/{id:guid}/active",async(Guid id,SetActiveRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new SetAgentDefinitionActiveCommand(id,r.IsActive,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.DefinitionsManage);

        var creds=root.MapGroup("/credentials");
        creds.MapGet("/",async(Guid? providerId,IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentCredentialDto>>.Ok(await d.DispatchAsync(new GetAgentCredentialsQuery(providerId),ct)))).RequireScope(AgentScopeNames.CredentialsView);
        creds.MapGet("/{id:guid}",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetAgentCredentialByIdQuery(id),ct),h)).RequireScope(AgentScopeNames.CredentialsView);
        creds.MapPost("/",async(CreateAgentCredentialRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateAgentCredentialCommand(r.ProviderId,r.Name,r.Username,r.Password,r.AdditionalSecretsJson,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.CredentialsManage);
        creds.MapPut("/{id:guid}",async(Guid id,UpdateAgentCredentialRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateAgentCredentialCommand(id,r.Name,r.Username,r.Password,r.AdditionalSecretsJson,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.CredentialsManage);
        creds.MapPatch("/{id:guid}/active",async(Guid id,SetActiveRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new SetAgentCredentialActiveCommand(id,r.IsActive,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.CredentialsManage);
        creds.MapPost("/{id:guid}/verify",async(Guid id,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new VerifyAgentCredentialCommand(id),ct),h)).RequireScope(AgentScopeNames.CredentialsVerify);

        var extractionProfiles=root.MapGroup("/extraction-profiles");
        extractionProfiles.MapGet("/",async(IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentExtractionProfileDto>>.Ok(await d.DispatchAsync(new GetAgentExtractionProfilesQuery(),ct)))).RequireScope(AgentScopeNames.ProvidersView);
        extractionProfiles.MapGet("/{id:guid}",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetAgentExtractionProfileByIdQuery(id),ct),h)).RequireScope(AgentScopeNames.ProvidersView);
        extractionProfiles.MapPost("/",async(CreateAgentExtractionProfileRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateAgentExtractionProfileCommand(r.ProviderId,r.CredentialId,r.Name,r.Description,r.BaseUrl,r.LoginUrl,r.SearchUrl,r.PromptTemplate,ParseEnum<AgentExecutionStrategy>(r.ExecutionStrategy),r.ParserKey,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.ProvidersManage);
        extractionProfiles.MapPut("/{id:guid}",async(Guid id,UpdateAgentExtractionProfileRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateAgentExtractionProfileCommand(id,r.CredentialId,r.Name,r.Description,r.BaseUrl,r.LoginUrl,r.SearchUrl,r.PromptTemplate,ParseEnum<AgentExecutionStrategy>(r.ExecutionStrategy),r.ParserKey,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.ProvidersManage);
        extractionProfiles.MapPatch("/{id:guid}/active",async(Guid id,SetActiveRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new SetAgentExtractionProfileActiveCommand(id,r.IsActive,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.ProvidersManage);
        extractionProfiles.MapDelete("/{id:guid}",async(Guid id,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new DeleteAgentExtractionProfileCommand(id,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.ProvidersManage);

        var extractionRoutes=root.MapGroup("/extraction-profiles/{profileId:guid}/routes");
        extractionRoutes.MapGet("/",async(Guid profileId,IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentExtractionRouteDto>>.Ok(await d.DispatchAsync(new GetExtractionRoutesQuery(profileId),ct)))).RequireScope(AgentScopeNames.RoutesManage);
        extractionRoutes.MapPost("/",async(Guid profileId,SaveAgentExtractionRouteRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateExtractionRouteCommand(profileId,r,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.RoutesManage);
        extractionRoutes.MapPut("/{routeId:guid}",async(Guid profileId,Guid routeId,SaveAgentExtractionRouteRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateExtractionRouteCommand(profileId,routeId,r,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.RoutesManage);
        extractionRoutes.MapDelete("/{routeId:guid}",async(Guid profileId,Guid routeId,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new DeleteExtractionRouteCommand(profileId,routeId,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.RoutesManage);

        var extractionEquipment=root.MapGroup("/extraction-profiles/{profileId:guid}/equipment");
        extractionEquipment.MapGet("/",async(Guid profileId,IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentExtractionEquipmentDto>>.Ok(await d.DispatchAsync(new GetExtractionEquipmentQuery(profileId),ct)))).RequireScope(AgentScopeNames.EquipmentManage);
        extractionEquipment.MapPost("/",async(Guid profileId,SaveAgentExtractionEquipmentRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateExtractionEquipmentCommand(profileId,r,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.EquipmentManage);
        extractionEquipment.MapPut("/{equipmentId:guid}",async(Guid profileId,Guid equipmentId,SaveAgentExtractionEquipmentRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateExtractionEquipmentCommand(profileId,equipmentId,r,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.EquipmentManage);
        extractionEquipment.MapDelete("/{equipmentId:guid}",async(Guid profileId,Guid equipmentId,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new DeleteExtractionEquipmentCommand(profileId,equipmentId,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.EquipmentManage);

        var endpointCaptures=root.MapGroup("/extraction-profiles/{profileId:guid}/captures");
        endpointCaptures.MapGet("/",async(Guid profileId,IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentEndpointCaptureDto>>.Ok(await d.DispatchAsync(new GetEndpointCapturesQuery(profileId),ct)))).RequireScope(AgentScopeNames.CaptureRulesManage);
        endpointCaptures.MapPost("/",async(Guid profileId,SaveAgentEndpointCaptureRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateEndpointCaptureCommand(profileId,r,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.CaptureRulesManage);
        endpointCaptures.MapPut("/{captureId:guid}",async(Guid profileId,Guid captureId,SaveAgentEndpointCaptureRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateEndpointCaptureCommand(profileId,captureId,r,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.CaptureRulesManage);
        endpointCaptures.MapDelete("/{captureId:guid}",async(Guid profileId,Guid captureId,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new DeleteEndpointCaptureCommand(profileId,captureId,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.CaptureRulesManage);
        endpointCaptures.MapPost("/{captureId:guid}/test",async(Guid profileId,Guid captureId,TestAgentEndpointCaptureRequest r,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new TestEndpointCaptureQuery(profileId,captureId,r),ct),h)).RequireScope(AgentScopeNames.CaptureRulesManage);

        var extractionFields=root.MapGroup("/extraction-profiles/{profileId:guid}/fields");
        extractionFields.MapGet("/",async(Guid profileId,IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentExtractionFieldDto>>.Ok(await d.DispatchAsync(new GetExtractionFieldsQuery(profileId),ct)))).RequireScope(AgentScopeNames.ExtractionFieldsManage);
        extractionFields.MapPost("/",async(Guid profileId,SaveAgentExtractionFieldRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateExtractionFieldCommand(profileId,r,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.ExtractionFieldsManage);
        extractionFields.MapPut("/{fieldId:guid}",async(Guid profileId,Guid fieldId,SaveAgentExtractionFieldRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateExtractionFieldCommand(profileId,fieldId,r,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.ExtractionFieldsManage);
        extractionFields.MapDelete("/{fieldId:guid}",async(Guid profileId,Guid fieldId,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new DeleteExtractionFieldCommand(profileId,fieldId,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.ExtractionFieldsManage);

        extractionProfiles.MapPost("/{profileId:guid}/prompt-preview",async(Guid profileId,AgentPromptPreviewRequest r,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetAgentPromptPreviewQuery(profileId,r),ct),h)).RequireScope(AgentScopeNames.PromptsManage);

        var profiles=root.MapGroup("/browser-profiles");
        profiles.MapGet("/",async(Guid? providerId,IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<BrowserProfileDto>>.Ok(await d.DispatchAsync(new GetBrowserProfilesQuery(providerId),ct)))).RequireScope(AgentScopeNames.BrowserProfilesView);
        profiles.MapGet("/{id:guid}",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetBrowserProfileByIdQuery(id),ct),h)).RequireScope(AgentScopeNames.BrowserProfilesView);
        profiles.MapPost("/",async(CreateBrowserProfileRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateBrowserProfileCommand(r.ProviderId,r.CredentialId,r.Name,r.ProfileKey,r.StoragePath,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);
        profiles.MapPost("/{id:guid}/authenticate",async(Guid id,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new AuthenticateBrowserProfileCommand(id,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);

        var schedules=root.MapGroup("/schedules");
        schedules.MapGet("/",async(IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentScheduleDto>>.Ok(await d.DispatchAsync(new GetAgentSchedulesQuery(),ct)))).RequireScope(AgentScopeNames.SchedulesView);
        schedules.MapGet("/{id:guid}",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetAgentScheduleByIdQuery(id),ct),h)).RequireScope(AgentScopeNames.SchedulesView);
        schedules.MapPost("/",async(CreateAgentScheduleRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateAgentScheduleCommand(r.Name,r.AgentDefinitionId,r.ProviderId,r.CredentialId,ParseEnum<AgentScheduleType>(r.ScheduleType),r.CronExpression,r.IntervalMinutes,r.ExecuteAt,r.Timezone,r.InputJson,r.MaxRetries,r.TimeoutSeconds,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.SchedulesCreate);
        schedules.MapPut("/{id:guid}",async(Guid id,UpdateAgentScheduleRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateAgentScheduleCommand(id,r.Name,r.CredentialId,ParseEnum<AgentScheduleType>(r.ScheduleType),r.CronExpression,r.IntervalMinutes,r.ExecuteAt,r.Timezone,r.InputJson,r.MaxRetries,r.TimeoutSeconds,r.NextExecutionAt,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.SchedulesUpdate);
        schedules.MapPatch("/{id:guid}/active",async(Guid id,SetActiveRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new SetAgentScheduleActiveCommand(id,r.IsActive,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.SchedulesUpdate);
        schedules.MapPost("/{id:guid}/run",async(Guid id,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new RunAgentScheduleCommand(id,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.SchedulesExecute);

        var executions=root.MapGroup("/executions");
        executions.MapGet("/",async(int? take,string? status,IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentExecutionDto>>.Ok(await d.DispatchAsync(new GetAgentExecutionsQuery(take??50,string.IsNullOrWhiteSpace(status)?null:ParseEnum<AgentExecutionStatus>(status)),ct)))).RequireScope(AgentScopeNames.ExecutionsView);
        executions.MapGet("/{id:guid}",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetAgentExecutionByIdQuery(id),ct),h)).RequireScope(AgentScopeNames.ExecutionsView);
        executions.MapGet("/{id:guid}/prompt",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetExecutionPromptSnapshotQuery(id),ct),h)).RequireScope(AgentScopeNames.ExecutionsView);
        executions.MapPost("/",async(CreateAgentExecutionRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateAgentExecutionCommand(r.AgentDefinitionId,r.ProviderId,r.CredentialId,r.Priority,r.InputJson,r.MaxAttempts,r.CorrelationId,r.TraceId,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.ExecutionsCreate);
        executions.MapPost("/{id:guid}/cancel",async(Guid id,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CancelAgentExecutionCommand(id,h.GetCurrentUserId()),ct),h)).RequireScope(AgentScopeNames.ExecutionsCancel);

        return app;
    }

    private static TEnum ParseEnum<TEnum>(string value) where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, true, out var parsed)) return parsed;
        throw new BadHttpRequestException($"Invalid {typeof(TEnum).Name}: {value}");
    }

    private sealed record SetActiveRequest(bool IsActive);
}
