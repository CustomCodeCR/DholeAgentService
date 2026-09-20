using CustomCodeFramework.Api.Responses;
using CustomCodeFramework.Cqrs.Commands;
using CustomCodeFramework.Cqrs.Queries;
using Dhole.Agent.Api.Extensions;
using Dhole.Agent.Application.Agents;
using Dhole.Agent.Contracts.Agents;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Api.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var root=app.MapGroup("/api/agents").WithTags("Agents").RequireAuthorization();

        var providers=root.MapGroup("/providers");
        providers.MapGet("/",async(IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentProviderDto>>.Ok(await d.DispatchAsync(new GetAgentProvidersQuery(),ct))));
        providers.MapGet("/{id:guid}",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetAgentProviderByIdQuery(id),ct),h));
        providers.MapPost("/",async(CreateAgentProviderRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateAgentProviderCommand(r.Code,r.Name,ParseEnum<AgentProviderType>(r.ProviderType),r.BaseUrl,ParseEnum<AgentExecutionStrategy>(r.DefaultExecutionStrategy),r.IsSystem,r.MetadataJson,h.GetCurrentUserId()),ct),h));
        providers.MapPut("/{id:guid}",async(Guid id,UpdateAgentProviderRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateAgentProviderCommand(id,r.Name,r.ProviderType,r.BaseUrl,r.DefaultExecutionStrategy,r.MetadataJson,h.GetCurrentUserId()),ct),h));
        providers.MapPatch("/{id:guid}/active",async(Guid id,SetActiveRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new SetAgentProviderActiveCommand(id,r.IsActive,h.GetCurrentUserId()),ct),h));

        var defs=root.MapGroup("/definitions");
        defs.MapGet("/",async(Guid? providerId,IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentDefinitionDto>>.Ok(await d.DispatchAsync(new GetAgentDefinitionsQuery(providerId),ct))));
        defs.MapGet("/{id:guid}",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetAgentDefinitionByIdQuery(id),ct),h));
        defs.MapPost("/",async(CreateAgentDefinitionRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateAgentDefinitionCommand(r.ProviderId,r.Code,r.Name,r.Description,ParseEnum<AgentActionType>(r.ActionType),ParseEnum<AgentExecutionStrategy>(r.ExecutionStrategy),r.ConfigurationJson,h.GetCurrentUserId()),ct),h));
        defs.MapPut("/{id:guid}",async(Guid id,UpdateAgentDefinitionRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateAgentDefinitionCommand(id,r.Name,r.Description,r.ActionType,r.ExecutionStrategy,r.ConfigurationJson,h.GetCurrentUserId()),ct),h));
        defs.MapPatch("/{id:guid}/active",async(Guid id,SetActiveRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new SetAgentDefinitionActiveCommand(id,r.IsActive,h.GetCurrentUserId()),ct),h));

        var creds=root.MapGroup("/credentials");
        creds.MapGet("/",async(Guid? providerId,IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentCredentialDto>>.Ok(await d.DispatchAsync(new GetAgentCredentialsQuery(providerId),ct))));
        creds.MapPost("/",async(CreateAgentCredentialRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateAgentCredentialCommand(r.ProviderId,r.Name,r.UsernameSecretKey,r.PasswordSecretKey,r.AdditionalSecretsJson,h.GetCurrentUserId()),ct),h));
        creds.MapPut("/{id:guid}",async(Guid id,UpdateAgentCredentialRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateAgentCredentialCommand(id,r.Name,r.UsernameSecretKey,r.PasswordSecretKey,r.AdditionalSecretsJson,h.GetCurrentUserId()),ct),h));
        creds.MapPatch("/{id:guid}/active",async(Guid id,SetActiveRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new SetAgentCredentialActiveCommand(id,r.IsActive,h.GetCurrentUserId()),ct),h));

        var profiles=root.MapGroup("/browser-profiles");
        profiles.MapGet("/",async(Guid? providerId,IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<BrowserProfileDto>>.Ok(await d.DispatchAsync(new GetBrowserProfilesQuery(providerId),ct))));
        profiles.MapGet("/{id:guid}",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetBrowserProfileByIdQuery(id),ct),h));
        profiles.MapPost("/",async(CreateBrowserProfileRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateBrowserProfileCommand(r.ProviderId,r.CredentialId,r.Name,r.ProfileKey,r.StoragePath,h.GetCurrentUserId()),ct),h));
        profiles.MapPost("/{id:guid}/authenticate",async(Guid id,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new AuthenticateBrowserProfileCommand(id,h.GetCurrentUserId()),ct),h));

        var schedules=root.MapGroup("/schedules");
        schedules.MapGet("/",async(IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentScheduleDto>>.Ok(await d.DispatchAsync(new GetAgentSchedulesQuery(),ct))));
        schedules.MapGet("/{id:guid}",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetAgentScheduleByIdQuery(id),ct),h));
        schedules.MapPost("/",async(CreateAgentScheduleRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateAgentScheduleCommand(r.Name,r.AgentDefinitionId,r.ProviderId,r.CredentialId,ParseEnum<AgentScheduleType>(r.ScheduleType),r.CronExpression,r.IntervalMinutes,r.ExecuteAt,r.Timezone,r.InputJson,r.MaxRetries,r.TimeoutSeconds,h.GetCurrentUserId()),ct),h));
        schedules.MapPut("/{id:guid}",async(Guid id,UpdateAgentScheduleRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new UpdateAgentScheduleCommand(id,r.Name,r.CredentialId,r.ScheduleType,r.CronExpression,r.IntervalMinutes,r.ExecuteAt,r.Timezone,r.InputJson,r.MaxRetries,r.TimeoutSeconds,r.NextExecutionAt,h.GetCurrentUserId()),ct),h));
        schedules.MapPatch("/{id:guid}/active",async(Guid id,SetActiveRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new SetAgentScheduleActiveCommand(id,r.IsActive,h.GetCurrentUserId()),ct),h));
        schedules.MapPost("/{id:guid}/run",async(Guid id,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new RunAgentScheduleCommand(id,h.GetCurrentUserId()),ct),h));

        var executions=root.MapGroup("/executions");
        executions.MapGet("/",async(int? take,string? status,IQueryDispatcher d,CancellationToken ct)=>Results.Ok(ApiResponse<IReadOnlyCollection<AgentExecutionDto>>.Ok(await d.DispatchAsync(new GetAgentExecutionsQuery(take??50,string.IsNullOrWhiteSpace(status)?null:ParseEnum<AgentExecutionStatus>(status)),ct))));
        executions.MapGet("/{id:guid}",async(Guid id,IQueryDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new GetAgentExecutionByIdQuery(id),ct),h));
        executions.MapPost("/",async(CreateAgentExecutionRequest r,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CreateAgentExecutionCommand(r.AgentDefinitionId,r.ProviderId,r.CredentialId,r.Priority,r.InputJson,r.MaxAttempts,r.CorrelationId,r.TraceId,h.GetCurrentUserId()),ct),h));
        executions.MapPost("/{id:guid}/cancel",async(Guid id,ICommandDispatcher d,HttpContext h,CancellationToken ct)=>EndpointResults.FromResult(await d.DispatchAsync(new CancelAgentExecutionCommand(id,h.GetCurrentUserId()),ct),h));

        return app;
    }

    private static TEnum ParseEnum<TEnum>(string value) where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, true, out var parsed)) return parsed;
        throw new BadHttpRequestException($"Invalid {typeof(TEnum).Name}: {value}");
    }

    private sealed record SetActiveRequest(bool IsActive);
}

