using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Contracts.Agents;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.Persistence.Repositories;

/// <summary>
/// Polls PostgreSQL state and writes deduplicated, auditable operational alerts.
/// No provider calls, browser repairs, session changes or automatic circuit resets.
/// </summary>
public sealed class PostgresMaerskMonitoring(
    ServiceDbContext db,
    IAgentProviderRepository providers,
    IOptions<MaerskMonitoringOptions> configured,
    IOptions<MaerskCircuitOptions> circuitConfigured,
    ILogger<PostgresMaerskMonitoring> logger) : IMaerskMonitoring
{
    private readonly MaerskMonitoringOptions _options = configured.Value;

    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled) return;
        _options.Validate();
        var provider = await providers.GetByCodeAsync("MAERSK", ct);
        if (provider is null || provider.IsDeleted) return;

        var now = DateTime.UtcNow;
        var metrics = await ObserveAsync(provider.Id, now, ct);
        var findings = MaerskMonitoringPolicy.Evaluate(metrics.Signals, _options);
        var active = findings.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var finding in findings)
        {
            var alertId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO agent.maersk_health_alerts
                    (id, provider_id, alert_key, code, severity, state, occurrences,
                     first_seen_at_utc, last_seen_at_utc)
                VALUES ({alertId}, {provider.Id}, {finding.Key}, {finding.Code},
                        {finding.Severity}, 'Active', 1, {now}, {now})
                ON CONFLICT (provider_id, alert_key)
                DO UPDATE SET
                    code = EXCLUDED.code,
                    severity = EXCLUDED.severity,
                    occurrences = CASE WHEN maersk_health_alerts.state = 'Resolved'
                        THEN maersk_health_alerts.occurrences + 1
                        ELSE maersk_health_alerts.occurrences END,
                    acknowledged_at_utc = CASE WHEN maersk_health_alerts.state = 'Resolved'
                        THEN NULL ELSE maersk_health_alerts.acknowledged_at_utc END,
                    acknowledged_by = CASE WHEN maersk_health_alerts.state = 'Resolved'
                        THEN NULL ELSE maersk_health_alerts.acknowledged_by END,
                    state = 'Active',
                    resolved_at_utc = NULL,
                    last_seen_at_utc = EXCLUDED.last_seen_at_utc
                """, ct);
        }

        foreach (var key in AllKeys)
        {
            if (active.Contains(key)) continue;
            await db.MaerskHealthAlerts
                .Where(x => x.ProviderId == provider.Id && x.AlertKey == key
                    && x.State == "Active" && x.LastSeenAtUtc <= now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.State, "Resolved")
                    .SetProperty(x => x.ResolvedAtUtc, now), ct);
        }
        await transaction.CommitAsync(ct);
        if (findings.Count > 0)
            logger.LogWarning(
                "MAERSK_MONITOR_ALERTS provider={ProviderId} activeCodes={Codes} queuedOld={QueuedOld} runningOld={RunningOld} recentFailures={Failures}",
                provider.Id, string.Join(",", findings.Select(x => x.Code)),
                metrics.LongQueued, metrics.LongRunning, metrics.FailedInWindow);
    }

    public async Task<MaerskHealthSnapshotDto> GetSnapshotAsync(
        Guid providerId, CancellationToken ct = default)
    {
        if (providerId == Guid.Empty) throw new ArgumentException("Provider required.", nameof(providerId));
        var now = DateTime.UtcNow;
        var metrics = await ObserveAsync(providerId, now, ct);

        IReadOnlyCollection<MaerskHealthAlertDto> alerts = [];
        if (_options.Enabled)
        {
            alerts = await db.MaerskHealthAlerts.AsNoTracking()
                .Where(x => x.ProviderId == providerId)
                .OrderBy(x => x.State == "Active" ? 0 : 1)
                .ThenByDescending(x => x.LastSeenAtUtc)
                .Take(50)
                .Select(x => new MaerskHealthAlertDto(
                    x.Id, x.AlertKey, x.Code, x.Severity, x.State,
                    x.FirstSeenAtUtc, x.LastSeenAtUtc, x.ResolvedAtUtc,
                    x.AcknowledgedAtUtc))
                .ToListAsync(ct);
        }

        return new MaerskHealthSnapshotDto(_options.Enabled, now,
            metrics.FailedInWindow, metrics.LongQueued, metrics.LongRunning,
            metrics.OldestQueued, metrics.LastCompleted, alerts);
    }

    public async Task<bool> AcknowledgeAsync(
        Guid providerId, Guid alertId, Guid actorId, CancellationToken ct = default)
    {
        if (!_options.Enabled) return false;
        if (providerId == Guid.Empty || alertId == Guid.Empty || actorId == Guid.Empty)
            return false;
        var now = DateTime.UtcNow;
        var updated = await db.MaerskHealthAlerts
            .Where(x => x.Id == alertId && x.ProviderId == providerId
                && x.State == "Active" && x.AcknowledgedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AcknowledgedAtUtc, now)
                .SetProperty(x => x.AcknowledgedBy, actorId), ct);
        if (updated == 1)
            logger.LogInformation("MAERSK_ALERT_ACKNOWLEDGED provider={ProviderId} alert={AlertId} actor={ActorId}",
                providerId, alertId, actorId);
        return updated == 1;
    }

    private async Task<Observed> ObserveAsync(Guid providerId, DateTime now, CancellationToken ct)
    {
        var executions = db.AgentExecutions.AsNoTracking()
            .Where(x => x.ProviderId == providerId);
        var queued = executions.Where(x =>
            x.Status == AgentExecutionStatus.Queued && x.CreatedAtUtc <= now);
        var failures = await executions.CountAsync(x => x.Status == AgentExecutionStatus.Failed
            && x.CompletedAt >= now.AddMinutes(-_options.FailureWindowMinutes), ct);
        var oldest = await queued.Select(x => (DateTime?)x.CreatedAtUtc)
            .MinAsync(ct);
        var queuedOld = await queued.CountAsync(x =>
            x.CreatedAtUtc <= now.AddMinutes(-_options.QueuedWarningMinutes)
            && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now), ct);
        var runningOld = await executions.CountAsync(x =>
            x.Status == AgentExecutionStatus.Running
            && x.StartedAt <= now.AddMinutes(-_options.RunningWarningMinutes), ct);
        var lastCompleted = await executions
            .Where(x => x.Status == AgentExecutionStatus.Completed
                || x.Status == AgentExecutionStatus.PartiallyCompleted)
            .Select(x => x.CompletedAt).MaxAsync(ct);
        var blockedProfiles = await db.BrowserProfiles.AsNoTracking().CountAsync(x =>
            x.ProviderId == providerId && x.IsActive && !x.IsDeleted
            && x.Status == BrowserProfileStatus.Blocked, ct);

        var circuitRequiresOperator = false;
        var halfOpenStalled = false;
        if (circuitConfigured.Value.Enabled)
        {
            var state = await db.MaerskCircuits.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProviderId == providerId, ct);
            circuitRequiresOperator = state?.State == "Open" && state.RequiresOperator;
            halfOpenStalled = state?.State == "HalfOpen"
                && state.UpdatedAtUtc <= now.AddMinutes(-_options.HalfOpenWarningMinutes);
        }

        return new Observed(failures, queuedOld, runningOld, oldest, lastCompleted,
            new MaerskHealthSignals(circuitRequiresOperator,
                halfOpenStalled, blockedProfiles, queuedOld, runningOld, failures));
    }

    private static readonly string[] AllKeys =
    [
        MaerskMonitoringPolicy.ProviderAccess,
        MaerskMonitoringPolicy.HalfOpen,
        MaerskMonitoringPolicy.BrowserBlocked,
        MaerskMonitoringPolicy.QueueBacklog,
        MaerskMonitoringPolicy.RunningStalled,
        MaerskMonitoringPolicy.FailedExecutions
    ];

    private sealed record Observed(int FailedInWindow, int LongQueued, int LongRunning,
        DateTime? OldestQueued, DateTime? LastCompleted, MaerskHealthSignals Signals);
}
