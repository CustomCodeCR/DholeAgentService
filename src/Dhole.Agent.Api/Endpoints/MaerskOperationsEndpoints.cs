using CustomCodeFramework.Api.Responses;
using Dhole.Agent.Api.Authorization;
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
            ServiceDbContext db,
            CancellationToken ct) =>
        {
            var provider = await providers.GetByCodeAsync("MAERSK", ct);
            if (provider is null || provider.IsDeleted)
                return Results.NotFound();

            var circuit = await breaker.GetAsync(provider.Id, ct);
            var enabled = circuitOptions.Value.Enabled;

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
                new MaerskOperationCircuitDto(enabled, circuit.State,
                    circuit.RequiresOperator, circuit.ReasonCode,
                    circuit.OpenUntilUtc, circuit.ConsecutiveFailures,
                    circuit.ProbeExecutionId),
                new MaerskOperationCountersDto(
                    Count(AgentExecutionStatus.Queued),
                    Count(AgentExecutionStatus.Running),
                    Count(AgentExecutionStatus.WaitingForAuthentication),
                    Count(AgentExecutionStatus.Completed) + Count(AgentExecutionStatus.PartiallyCompleted),
                    Count(AgentExecutionStatus.Failed)),
                profiles, recent, events);
            return Results.Ok(ApiResponse<MaerskOperationsDto>.Ok(result));
        })
        .RequireScope(AgentScopeNames.ExecutionsView)
        .RequireScope(AgentScopeNames.BrowserProfilesView);
    }
}
