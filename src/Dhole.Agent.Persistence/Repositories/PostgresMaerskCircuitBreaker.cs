using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.Persistence.Repositories;

/// <summary>
/// PostgreSQL-backed provider-wide Maersk circuit. A manual reset must be
/// authorized and attested; provider restrictions never auto-expire.
/// </summary>
public sealed class PostgresMaerskCircuitBreaker(
    ServiceDbContext db,
    IOptions<MaerskCircuitOptions> configured) : IMaerskCircuitBreaker
{
    private readonly MaerskCircuitOptions _options = configured.Value;

    public async Task<MaerskCircuitSnapshot> GetAsync(Guid providerId, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return MaerskCircuitSnapshot.Disabled(providerId);
        ValidateProvider(providerId);
        _options.Validate();
        var row = await db.Set<MaerskCircuitRecord>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProviderId == providerId, ct);
        return row is null
            ? MaerskCircuitSnapshot.Closed(providerId)
            : new MaerskCircuitSnapshot(row.ProviderId, row.State, row.RequiresOperator,
                row.ReasonCode, row.OpenUntilUtc, row.ConsecutiveFailures, row.ProbeExecutionId);
    }

    public async Task<bool> CanScheduleAsync(Guid providerId, CancellationToken ct = default)
        => (await GetAsync(providerId, ct)).CanSchedule(DateTime.UtcNow);

    public async Task<bool> TryEnterAsync(Guid providerId, Guid executionId, CancellationToken ct = default)
    {
        if (!_options.Enabled) return true;
        ValidateProvider(providerId);
        _options.Validate();
        if (executionId == Guid.Empty) throw new ArgumentException("Execution ID required.", nameof(executionId));

        await EnsureRowAsync(providerId, ct);

        // Atomic compare-and-swap: after a technical cooldown only ONE job may
        // claim the half-open probe. Provider verifications never use this path.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE agent.maersk_circuits
            SET state = 'HalfOpen',
                probe_execution_id = {executionId},
                updated_at_utc = NOW()
            WHERE provider_id = {providerId}
              AND state = 'Open'
              AND requires_operator = false
              AND open_until_utc <= NOW()
            """, ct);

        var state = await GetAsync(providerId, ct);
        return state.State == "Closed"
            || (state.State == "HalfOpen" && state.ProbeExecutionId == executionId);
    }

    public async Task RecordFailureAsync(
        Guid providerId, Guid executionId, string errorCode, CancellationToken ct = default)
    {
        if (!_options.Enabled) return;
        ValidateProvider(providerId);
        _options.Validate();
        if (executionId == Guid.Empty) throw new ArgumentException("Execution ID required.", nameof(executionId));

        var policy = MaerskFailureClassifier.Classify(errorCode);
        var requiresOperator = policy.ShouldOpenCircuit || policy.RequiresOperator;
        var providerTransient = policy.Category == FailureCategory.ProviderOrUiTimeout
            && !policy.RequiresOperator
            && policy.CanonicalErrorCode is "maersk_offer_timeout"
                or "maersk_provider_service_unavailable"
                or "maersk_authentication_service_error";

        // The DB transaction serializes changes by provider. When the same
        // execution is redelivered, its failure is counted exactly once.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await EnsureRowAsync(providerId, ct);
        var previousRow = await db.Set<MaerskCircuitRecord>().FromSqlInterpolated(
            $"SELECT * FROM agent.maersk_circuits WHERE provider_id = {providerId} FOR UPDATE")
            .AsNoTracking().SingleAsync(ct);

        if (previousRow.RequiresOperator
            || (!requiresOperator && !providerTransient && previousRow.State != "HalfOpen"))
        {
            await tx.CommitAsync(ct);
            return;
        }

        var normalized = policy.CanonicalErrorCode.Length > 120
            ? "maersk_unclassified_error" : policy.CanonicalErrorCode;
        var observed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO agent.maersk_circuit_events
                (id,provider_id,execution_id,event_type,reason_code)
            VALUES ({Guid.NewGuid()},{providerId},{executionId},'FailureObserved',{normalized})
            ON CONFLICT (provider_id,execution_id,event_type,reason_code)
                WHERE execution_id IS NOT NULL DO NOTHING
            """, ct);
        if (observed == 0)
        {
            await tx.CommitAsync(ct);
            return;
        }

        var threshold = _options.TransientFailureThreshold;
        var technicalCooldown = _options.TechnicalCooldownSeconds;
        var providerCooldown = _options.ProviderCooldownSeconds;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE agent.maersk_circuits
            SET consecutive_failures = CASE
                    WHEN {requiresOperator} THEN GREATEST(consecutive_failures, 1)
                    WHEN {providerTransient} THEN consecutive_failures + 1
                    ELSE GREATEST(consecutive_failures, 1) END,
                state = CASE
                    WHEN requires_operator OR {requiresOperator} THEN 'Open'
                    WHEN state = 'HalfOpen' THEN 'Open'
                    WHEN {providerTransient} AND consecutive_failures + 1 >= {threshold} THEN 'Open'
                    ELSE state END,
                requires_operator = requires_operator OR {requiresOperator},
                open_until_utc = CASE
                    WHEN requires_operator THEN open_until_utc
                    WHEN {requiresOperator}
                        THEN NOW() + ({providerCooldown} * INTERVAL '1 second')
                    WHEN state = 'HalfOpen'
                        OR ({providerTransient} AND consecutive_failures + 1 >= {threshold})
                        THEN NOW() + ({technicalCooldown} * INTERVAL '1 second')
                    ELSE open_until_utc END,
                probe_execution_id = NULL,
                reason_code = CASE WHEN requires_operator THEN reason_code ELSE {normalized} END,
                updated_at_utc = NOW()
            WHERE provider_id = {providerId}
            """, ct);

        var current = await db.Set<MaerskCircuitRecord>().AsNoTracking()
            .SingleAsync(x => x.ProviderId == providerId, ct);
        if (current.State == "Open"
            && (previousRow.State != "Open" || previousRow.ReasonCode != current.ReasonCode))
            await AuditAsync(providerId, "Opened", normalized, null, null, ct);
        await tx.CommitAsync(ct);
    }

    public async Task RecordSuccessAsync(Guid providerId, Guid executionId, CancellationToken ct = default)
    {
        if (!_options.Enabled) return;
        ValidateProvider(providerId);
        var previous = await GetAsync(providerId, ct);
        if (previous.State == "Open" || previous.RequiresOperator) return;

        var changed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE agent.maersk_circuits
            SET state = 'Closed', requires_operator = false,
                reason_code = NULL, open_until_utc = NULL,
                consecutive_failures = 0, probe_execution_id = NULL,
                updated_at_utc = NOW()
            WHERE provider_id = {providerId}
              AND requires_operator = false
              AND (state = 'Closed'
                   OR (state = 'HalfOpen' AND probe_execution_id = {executionId}))
            """, ct);

        if (changed > 0 && previous.State == "HalfOpen")
            await AuditAsync(providerId, "ProbeSucceeded", null, null, null, ct);
    }

    public async Task<bool> ResetByOperatorAsync(
        Guid providerId, Guid actorId, string reason,
        bool verifiedWithProvider, CancellationToken ct = default)
    {
        if (!_options.Enabled) return false;
        ValidateProvider(providerId);
        if (actorId == Guid.Empty || !verifiedWithProvider
            || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 12
            || reason.Length > 1000)
            throw new ArgumentException(
                "An authenticated operator, documented reason and verified provider clearance are required.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await db.Set<MaerskCircuitRecord>().FromSqlInterpolated(
            $"SELECT * FROM agent.maersk_circuits WHERE provider_id = {providerId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (current is null || current.State is not ("Open" or "HalfOpen"))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        if (current.RequiresOperator)
        {
            // A request body is not proof of provider recovery. Require a
            // successful authentication recorded AFTER the restriction and
            // reject blocked/expired active profiles. No Chromium action or
            // browser profile repair is initiated by this endpoint.
            var now = DateTime.UtcNow;
            var latestVerifiedSession = await db.BrowserProfiles.AsNoTracking()
                .AnyAsync(p => p.ProviderId == providerId
                    && !p.IsDeleted && p.IsActive
                    && p.Status == BrowserProfileStatus.Authenticated
                    && p.LastLoginAt.HasValue
                    && p.LastLoginAt.Value > current.UpdatedAtUtc
                    && p.LastLoginAt.Value <= now
                    && (!p.SessionExpiresAt.HasValue || p.SessionExpiresAt.Value > now), ct);
            var unsafeProfile = await db.BrowserProfiles.AsNoTracking()
                .AnyAsync(p => p.ProviderId == providerId
                    && !p.IsDeleted && p.IsActive
                    && (p.Status == BrowserProfileStatus.Blocked
                        || p.Status == BrowserProfileStatus.Expired
                        || p.Status == BrowserProfileStatus.ResetRequested), ct);
            if (!latestVerifiedSession || unsafeProfile)
            {
                await tx.CommitAsync(ct);
                return false;
            }
        }

        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE agent.maersk_circuits
            SET state = 'Closed', requires_operator = false,
                reason_code = NULL, open_until_utc = NULL,
                consecutive_failures = 0, probe_execution_id = NULL,
                updated_at_utc = NOW()
            WHERE provider_id = {providerId}
              AND state IN ('Open','HalfOpen')
              -- Fail closed even for legacy Running jobs without leases.
              AND NOT EXISTS (
                  SELECT 1 FROM agent."AgentExecutions" e
                  WHERE e.provider_id = {providerId}
                    AND e.status = 'Running'
              )
            """, ct);
        if (affected == 1)
            await AuditAsync(providerId, "OperatorReset", null, actorId, reason.Trim(), ct);
        await tx.CommitAsync(ct);
        return affected == 1;
    }

    private async Task EnsureRowAsync(Guid providerId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO agent.maersk_circuits(provider_id)
            VALUES ({providerId})
            ON CONFLICT (provider_id) DO NOTHING
            """, ct);
    }

    private async Task AuditAsync(Guid providerId, string eventType, string? reasonCode,
        Guid? actorId, string? operatorReason, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO agent.maersk_circuit_events
                (id,provider_id,event_type,reason_code,actor_id,operator_reason)
            VALUES ({id},{providerId},{eventType},{reasonCode},{actorId},{operatorReason})
            """, ct);
    }

    private static void ValidateProvider(Guid providerId)
    {
        if (providerId == Guid.Empty)
            throw new ArgumentException("Provider ID required.", nameof(providerId));
    }
}
