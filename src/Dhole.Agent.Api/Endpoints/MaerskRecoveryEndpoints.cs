using CustomCodeFramework.Api.Responses;
using Dhole.Agent.Api.Authorization;
using Dhole.Agent.Api.Extensions;
using Dhole.Agent.Contracts.Agents;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Dhole.Agent.Persistence.Repositories;
using Dhole.Agent.Application.Abstractions.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.Api.Endpoints;

/// <summary>
/// Phase 5 operational API. Only redacted metadata is returned.
/// This API never opens Chromium, resets cookies or bypasses provider verification.
/// </summary>
internal static class MaerskRecoveryEndpoints
{
    public static void MapMaerskRecoveryEndpoints(this RouteGroupBuilder root)
    {
        root.MapGet("/maersk/profiles/{profileId:guid}/health", async (
            Guid profileId, ServiceDbContext db,
            IMaerskCircuitBreaker circuit,
            IHostEnvironment environment, CancellationToken ct) =>
        {
            var profile = await db.BrowserProfiles.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == profileId && !x.IsDeleted, ct);
            if (profile is null)
                return Results.NotFound();

            var provider = await db.AgentProviders.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == profile.ProviderId && !x.IsDeleted, ct);
            if (provider is null || !provider.Code.Equals("MAERSK", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();

            var scope = db.AgentExecutions.AsNoTracking()
                .Where(x => x.ProviderId == provider.Id && x.CredentialId == profile.CredentialId);
            var waitingCount = await scope.CountAsync(
                x => x.Status == AgentExecutionStatus.WaitingForAuthentication, ct);
            var queuedCount = await scope.CountAsync(
                x => x.Status == AgentExecutionStatus.Queued, ct);
            var runningCount = await scope.CountAsync(
                x => x.Status == AgentExecutionStatus.Running, ct);
            var lastSuccess = await scope
                .Where(x => x.Status == AgentExecutionStatus.Completed
                    || x.Status == AgentExecutionStatus.PartiallyCompleted)
                .OrderByDescending(x => x.CompletedAt)
                .Select(x => x.CompletedAt).FirstOrDefaultAsync(ct);
            var mostRecentError = await scope
                .Where(x => x.ErrorCode != null)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Select(x => x.ErrorCode).FirstOrDefaultAsync(ct);
            // Untrusted provider messages never enter the response.
            var safeError = !string.IsNullOrWhiteSpace(mostRecentError)
                && mostRecentError.Length <= 120
                && mostRecentError.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')
                    ? mostRecentError : null;

            var state = await circuit.GetAsync(provider.Id, ct);
            var now = DateTime.UtcNow;
            var sessionReady = profile.IsActive
                && profile.Status == BrowserProfileStatus.Authenticated
                && profile.LastLoginAt.HasValue
                && (!profile.SessionExpiresAt.HasValue || profile.SessionExpiresAt > now);
            var nextAction = state.State == "Disabled"
                ? "CircuitNotEnabled"
                : state.RequiresOperator || profile.Status == BrowserProfileStatus.Blocked
                    ? "VerifyProviderManually"
                    : profile.Status is BrowserProfileStatus.Expired or BrowserProfileStatus.LoginRequired
                        ? "AuthenticateOriginalProfile"
                        : profile.Status is BrowserProfileStatus.Error or BrowserProfileStatus.ResetRequested
                            ? "ReviewTechnicalRepair"
                            : state.State != "Closed"
                                ? "WaitForCircuit"
                                : sessionReady && waitingCount > 0
                                    ? "ReviewWaitingExecution"
                                    : sessionReady ? "SessionRecordedAsAuthenticated" : "ReviewSession";

            var dto = new MaerskProfileHealthDto(
                environment.EnvironmentName,
                provider.Id, profile.Id, profile.Name,
                profile.Status.ToString(), profile.IsActive,
                state.State, state.RequiresOperator,
                safeError, profile.LastLoginAt, profile.LastUsedAt,
                profile.SessionExpiresAt, lastSuccess,
                queuedCount, runningCount, waitingCount, nextAction);
            return Results.Ok(ApiResponse<MaerskProfileHealthDto>.Ok(dto));
        })
        .RequireScope(AgentScopeNames.BrowserProfilesView)
        .RequireScope(AgentScopeNames.ExecutionsView);

        root.MapGet("/maersk/executions/waiting", async (
            int? take, ServiceDbContext db, CancellationToken ct) =>
        {
            var providers = db.AgentProviders.AsNoTracking()
                .Where(x => !x.IsDeleted && x.Code == "MAERSK");
            var waiting = await db.AgentExecutions.AsNoTracking()
                .Where(x => providers.Any(p => p.Id == x.ProviderId)
                    && x.Status == AgentExecutionStatus.WaitingForAuthentication)
                .OrderBy(x => x.CreatedAtUtc)
                .Take(Math.Clamp(take ?? 50, 1, 100))
                .Select(x => new MaerskOperationExecutionDto(
                    x.Id, x.Status.ToString(), x.ExecutionType.ToString(),
                    x.Attempt, x.MaxAttempts, x.ErrorCode,
                    x.CreatedAtUtc, x.StartedAt, x.CompletedAt, x.CorrelationId))
                .ToArrayAsync(ct);
            return Results.Ok(ApiResponse<MaerskOperationExecutionDto[]>.Ok(waiting));
        })
        .RequireScope(AgentScopeNames.ExecutionsView)
        .RequireScope(AgentScopeNames.BrowserProfilesView);

        root.MapPost("/maersk/executions/{executionId:guid}/resume", async (
            Guid executionId, MaerskResumeExecutionRequest request,
            HttpContext context, MaerskExecutionResumeService resumes,
            CancellationToken ct) =>
        {
            var actor = context.GetCurrentUserId();
            if (actor is null || actor == Guid.Empty)
                return Results.Unauthorized();
            if (!request.VerifiedWithProvider || string.IsNullOrWhiteSpace(request.Reason)
                || request.Reason.Trim().Length is < 12 or > 500)
                return Results.BadRequest(new
                {
                    error = "Authorized provider verification and a reason of 12-500 characters are required."
                });
            var outcome = await resumes.ResumeAsync(
                executionId, actor.Value, request.Reason, request.VerifiedWithProvider, ct);
            return outcome switch
            {
                MaerskResumeOutcome.Resumed => Results.Ok(new
                    { resumed = true, executionId, alreadyQueued = false }),
                MaerskResumeOutcome.AlreadyQueued => Results.Ok(new
                    { resumed = true, executionId, alreadyQueued = true }),
                MaerskResumeOutcome.NotFound => Results.NotFound(),
                _ => Results.Conflict(new { error = outcome.ToString() })
            };
        })
        .RequireScope(AgentScopeNames.BrowserProfilesAuthenticate)
        .RequireScope(AgentScopeNames.ExecutionsCreate);
    }

    private sealed record MaerskResumeExecutionRequest(string Reason, bool VerifiedWithProvider);
}
