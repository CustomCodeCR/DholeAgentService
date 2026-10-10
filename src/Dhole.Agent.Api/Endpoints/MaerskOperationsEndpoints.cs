using CustomCodeFramework.Api.Responses;
using Dhole.Agent.Api.Authorization;
using Dhole.Agent.Api.Extensions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Contracts.Agents;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.Api.Endpoints;

/// <summary>
/// Read-only, scope-protected operations snapshot. No raw browser state, session
/// cookies, credentials, input JSON or provider error bodies are returned.
/// Administrative reset continues to use the authorized phase-4 endpoint.
/// </summary>
internal static class MaerskOperationsEndpoints
{
    public static void MapMaerskOperationsEndpoints(this RouteGroupBuilder root)
    {
        root.MapGet("/maersk/operations", async (
            IAgentProviderRepository providers,
            IMaerskCircuitBreaker breaker,
            IOptions<MaerskCircuitOptions> circuitOptions,
            IConfiguration configuration,
            IMaerskMonitoring monitoring,
            ServiceDbContext db,
            CancellationToken ct) =>
        {
            var provider = await providers.GetByCodeAsync("MAERSK", ct);
            if (provider is null || provider.IsDeleted)
                return Results.NotFound();

            var enabled = circuitOptions.Value.Enabled;
            // A disabled circuit is not a healthy Closed circuit.
            // Keep the last persisted state separate from current protection.
            var circuit = await breaker.GetAsync(provider.Id, ct);
            var persisted = await db.MaerskCircuits.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProviderId == provider.Id, ct);
            var effectiveSource = configuration.AsEnumerable()
                .Any(x => x.Key == "MaerskCircuit:Enabled")
                    ? "ExplicitConfiguration" : "Default";
            var reportedState = enabled ? circuit.State : "Disabled";

            var query = db.AgentExecutions.AsNoTracking().Where(x => x.ProviderId == provider.Id);
            var counts = await query
                .GroupBy(x => x.Status)
                .Select(group => new { Status = group.Key, Count = group.Count() })
                .ToListAsync(ct);
            int Count(AgentExecutionStatus status)
                => counts.FirstOrDefault(x => x.Status == status)?.Count ?? 0;

            var recent = await query.OrderByDescending(x => x.CreatedAtUtc).Take(40)
                .Select(x => new MaerskOperationExecutionDto(
                    x.Id, x.Status.ToString(), x.ExecutionType.ToString(),
                    x.Attempt, x.MaxAttempts, x.ErrorCode,
                    x.CreatedAtUtc, x.StartedAt, x.CompletedAt, x.CorrelationId))
                .ToListAsync(ct);

            var profiles = await db.BrowserProfiles.AsNoTracking()
                .Where(x => x.ProviderId == provider.Id && !x.IsDeleted)
                .OrderBy(x => x.Name)
                .Select(x => new MaerskOperationProfileDto(
                    x.Id, x.Name, x.Status.ToString(),
                    x.LastLoginAt, x.LastUsedAt, x.SessionExpiresAt))
                .ToListAsync(ct);

            // The phase-4 migration precedes this query. When the circuit
            // feature is off, an older environment may lack the event table.
            IReadOnlyCollection<MaerskOperationEventDto> events =
                Array.Empty<MaerskOperationEventDto>();
            if (enabled)
            {
                events = await db.MaerskCircuitEvents.AsNoTracking()
                    .Where(x => x.ProviderId == provider.Id)
                    .OrderByDescending(x => x.OccurredAtUtc).Take(30)
                    .Select(x => new MaerskOperationEventDto(
                        x.Id, x.EventType, x.ReasonCode, x.ActorId, x.OccurredAtUtc))
                    .ToListAsync(ct);
            }

            var result = new MaerskOperationsDto(
                provider.Id, provider.Name, DateTime.UtcNow,
                new MaerskOperationCircuitDto(enabled, reportedState,
                    enabled && circuit.RequiresOperator, enabled ? circuit.ReasonCode : null,
                    enabled ? circuit.OpenUntilUtc : null,
                    enabled ? circuit.ConsecutiveFailures : 0,
                    enabled ? circuit.ProbeExecutionId : null)
                {
                    UpdatedAtUtc = persisted?.UpdatedAtUtc,
                    PersistedState = persisted?.State,
                    ConfigurationSource = effectiveSource
                },
                new MaerskOperationCountersDto(
                    Count(AgentExecutionStatus.Queued),
                    Count(AgentExecutionStatus.Running),
                    Count(AgentExecutionStatus.WaitingForAuthentication),
                    Count(AgentExecutionStatus.Completed) + Count(AgentExecutionStatus.PartiallyCompleted),
                    Count(AgentExecutionStatus.Failed)),
                profiles, recent, events,
                await monitoring.GetSnapshotAsync(provider.Id, ct));
            return Results.Ok(ApiResponse<MaerskOperationsDto>.Ok(result));
        })
        .RequireScope(AgentScopeNames.ExecutionsView)
        .RequireScope(AgentScopeNames.BrowserProfilesView);

        // Acknowledgement records that a human has seen the alert.
        // It never closes the provider circuit, retries jobs, or repairs profiles.
        root.MapPost("/maersk/alerts/{alertId:guid}/acknowledge", async (
            Guid alertId,
            HttpContext context,
            IAgentProviderRepository providers,
            IMaerskMonitoring monitoring,
            CancellationToken ct) =>
        {
            var actor = context.GetCurrentUserId();
            if (actor is null || actor.Value == Guid.Empty)
                return Results.Unauthorized();

            var provider = await providers.GetByCodeAsync("MAERSK", ct);
            if (provider is null || provider.IsDeleted)
                return Results.NotFound();

            var updated = await monitoring.AcknowledgeAsync(provider.Id,
                alertId, actor.Value, ct);
            return updated
                ? Results.Ok(new { acknowledged = true, alertId })
                : Results.Conflict(new
                {
                    error = "Alert not active, previously acknowledged or monitoring is disabled."
                });
        })
        .RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);
    }
}
