using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dhole.Agent.Persistence.Repositories;

/// <summary>
/// PostgreSQL is the only execution owner. A unique lease_scope atomically
/// prevents duplicate dispatch among workers and serializes Maersk globally.
/// The lease is independent of Redis availability and of any EF DbContext
/// used by an actual browser execution.
/// </summary>
public sealed class PostgresAgentQueueLeaseStore(ServiceDbContext db) : IAgentQueueLeaseStore
{
    public async Task<bool> TryClaimAsync(
        Guid executionId, string scopeKey, Guid ownerId, TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        Validate(scopeKey, ownerId, lease);
        var seconds = (int)lease.TotalSeconds;
        var maersk = scopeKey.StartsWith("maersk:", StringComparison.Ordinal);

        // The PostgreSQL scope PRIMARY KEY serializes claims, including attempts
        // from other workers. For Maersk, reject a transition while an older
        // (pre-rollout) Running execution is still active on this provider.
        int written;
        try
        {
            written = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO agent.execution_leases
                (lease_scope, execution_id, owner_id, expires_at_utc, heartbeat_at_utc)
            SELECT {scopeKey}, e.id, {ownerId},
                   NOW() + ({seconds} * INTERVAL '1 second'), NOW()
            FROM agent."AgentExecutions" e
            WHERE e.id = {executionId}
              AND e.status = 'Queued'
              AND e.attempt < e.max_attempts
              AND (e.next_attempt_at_utc IS NULL OR e.next_attempt_at_utc <= NOW())
              AND (NOT {maersk} OR NOT EXISTS (
                    SELECT 1 FROM agent."AgentExecutions" running
                    WHERE running.provider_id = e.provider_id
                      AND running.status = 'Running'
                      AND running.id <> e.id
                  ))
            ON CONFLICT (lease_scope) DO UPDATE SET
                execution_id = EXCLUDED.execution_id,
                owner_id = EXCLUDED.owner_id,
                expires_at_utc = EXCLUDED.expires_at_utc,
                heartbeat_at_utc = EXCLUDED.heartbeat_at_utc
            WHERE agent.execution_leases.expires_at_utc < NOW();
            """, cancellationToken);
        }
        catch (PostgresException error)
            when (error.SqlState == PostgresErrorCodes.UniqueViolation
                  && error.ConstraintName == "ux_agent_execution_leases_execution")
        {
            // Concurrent workers can race on the execution_id uniqueness
            // constraint before the lease_scope UPSERT arbitration completes.
            // The other worker owns the attempt; never treat it as a fatal
            // dispatch error or retry concurrently.
            return false;
        }

        return written == 1;
    }

    public async Task<bool> RenewAsync(
        string scopeKey, Guid executionId, Guid ownerId, TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        Validate(scopeKey, ownerId, lease);
        var seconds = (int)lease.TotalSeconds;
        var written = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE agent.execution_leases
            SET expires_at_utc = NOW() + ({seconds} * INTERVAL '1 second'),
                heartbeat_at_utc = NOW()
            WHERE lease_scope = {scopeKey}
              AND execution_id = {executionId}
              AND owner_id = {ownerId}
              AND expires_at_utc > NOW()
            """, cancellationToken);
        return written == 1;
    }

    public async Task ReleaseAsync(
        string scopeKey, Guid executionId, Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM agent.execution_leases
            WHERE lease_scope = {scopeKey}
              AND execution_id = {executionId}
              AND owner_id = {ownerId}
            """, cancellationToken);
    }

    public Task<int> FailInterruptedAsync(CancellationToken cancellationToken = default)
        => FailInterruptedCoreAsync(cancellationToken);

    private async Task<int> FailInterruptedCoreAsync(CancellationToken cancellationToken)
    {
        var expired = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE agent."AgentExecutions" e
            SET status = 'Failed',
                error_code = 'agent_worker_interrupted',
                error_message = 'Worker lease expired. Execution was not replayed because provider side effects might already have occurred.',
                completed_at = NOW(),
                updated_at_utc = NOW()
            FROM agent.execution_leases l
            WHERE l.execution_id = e.id
              AND e.status = 'Running'
              AND l.expires_at_utc < NOW()
            """, cancellationToken);

        // Legacy Running entries (from before the lease migration) are never
        // automatically replayed. After a conservative age threshold, record a
        // terminal interrupted state instead of leaving the queue stuck forever.
        var legacy = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE agent."AgentExecutions" e
            SET status = 'Failed',
                error_code = 'agent_legacy_execution_interrupted',
                error_message = 'Legacy Running execution exceeded the recovery safety window; review manually before repeating.',
                completed_at = NOW(),
                updated_at_utc = NOW()
            WHERE e.status = 'Running'
              AND COALESCE(e.started_at, e.created_at_utc) < NOW() - INTERVAL '12 hours'
              AND NOT EXISTS (
                  SELECT 1 FROM agent.execution_leases l WHERE l.execution_id = e.id
              )
            """, cancellationToken);
        return expired + legacy;
    }

    public Task<int> FailAttemptsExhaustedAsync(CancellationToken cancellationToken = default)
        => db.Database.ExecuteSqlRawAsync(
            """
            UPDATE agent."AgentExecutions"
            SET status = 'Failed',
                error_code = 'agent_max_attempts_exceeded',
                error_message = 'The configured maximum execution attempts were exhausted.',
                completed_at = NOW(),
                updated_at_utc = NOW()
            WHERE status = 'Queued' AND attempt >= max_attempts
            """, cancellationToken);

    private static void Validate(string scopeKey, Guid ownerId, TimeSpan lease)
    {
        if (string.IsNullOrWhiteSpace(scopeKey) || scopeKey.Length > 160)
            throw new ArgumentException("Invalid queue lease scope.", nameof(scopeKey));
        if (ownerId == Guid.Empty)
            throw new ArgumentException("An owner ID is required.", nameof(ownerId));
        if (lease < TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(lease));
    }
}
