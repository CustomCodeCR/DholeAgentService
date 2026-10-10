using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.Persistence.Repositories;

public enum MaerskResumeOutcome
{
    Resumed,
    AlreadyQueued,
    NotFound,
    NotWaiting,
    CircuitUnavailable,
    ProviderVerificationRequired,
    ProfileNotVerified,
    ProviderBusy
}

/// <summary>
/// Transactional, auditable resume of exactly one previously waiting execution.
/// Preserves execution ID, input, prompt, configuration and correlation.
/// Never performs network requests to Maersk or manipulates browser state.
/// </summary>
public sealed class MaerskExecutionResumeService(
    ServiceDbContext db,
    IOptions<MaerskCircuitOptions> options)
{
    public async Task<MaerskResumeOutcome> ResumeAsync(
        Guid executionId, Guid actorId, string reason, bool verifiedWithProvider,
        CancellationToken ct = default)
    {
        if (executionId == Guid.Empty || actorId == Guid.Empty
            || !verifiedWithProvider || string.IsNullOrWhiteSpace(reason)
            || reason.Trim().Length is < 12 or > 500)
            throw new ArgumentException("Authenticated operator, verification and reason are required.");

        // Disabled/unknown protection cannot be considered a Closed circuit.
        if (!options.Value.Enabled) return MaerskResumeOutcome.CircuitUnavailable;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var execution = await db.AgentExecutions
            .SingleOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null)
            return MaerskResumeOutcome.NotFound;
        var provider = await db.AgentProviders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == execution.ProviderId, ct);
        if (provider is null || provider.IsDeleted
            || !provider.Code.Equals("MAERSK", StringComparison.OrdinalIgnoreCase))
            return MaerskResumeOutcome.NotFound;

        // Serialize all authorized resumes using the same provider circuit row.
        // An operator cannot flood the queue with blocked jobs.
        var circuit = await db.MaerskCircuits.FromSqlInterpolated(
            $"SELECT * FROM agent.maersk_circuits WHERE provider_id = {execution.ProviderId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (circuit is null || circuit.State != "Closed" || circuit.RequiresOperator)
            return MaerskResumeOutcome.CircuitUnavailable;

        if (execution.Status == AgentExecutionStatus.Queued)
            return MaerskResumeOutcome.AlreadyQueued;
        if (execution.Status != AgentExecutionStatus.WaitingForAuthentication)
            return MaerskResumeOutcome.NotWaiting;
        if (execution.CredentialId is not Guid credentialId)
            return MaerskResumeOutcome.ProfileNotVerified;

        // Never bypass a fresh provider challenge with a previously recorded login.
        var incident = await db.MaerskCircuitEvents.AsNoTracking()
            .Where(e => e.ProviderId == execution.ProviderId && e.EventType == "Opened")
            .OrderByDescending(e => e.OccurredAtUtc)
            .Select(e => new { e.Id, e.OccurredAtUtc })
            .FirstOrDefaultAsync(ct);
        if (incident is null)
            return MaerskResumeOutcome.ProviderVerificationRequired;

        var now = DateTime.UtcNow;
        var usable = await db.BrowserProfiles.AsNoTracking()
            .AnyAsync(p => p.ProviderId == execution.ProviderId
                && p.CredentialId == credentialId
                && p.IsActive && !p.IsDeleted
                && p.Status == BrowserProfileStatus.Authenticated
                && p.LastLoginAt.HasValue
                && p.LastLoginAt.Value > incident.OccurredAtUtc
                && p.LastLoginAt.Value <= now
                && (!p.SessionExpiresAt.HasValue || p.SessionExpiresAt.Value > now), ct);
        var unsafeProfile = await db.BrowserProfiles.AsNoTracking()
            .AnyAsync(p => p.ProviderId == execution.ProviderId
                && p.CredentialId == credentialId && p.IsActive && !p.IsDeleted
                && (p.Status == BrowserProfileStatus.Blocked
                    || p.Status == BrowserProfileStatus.Expired
                    || p.Status == BrowserProfileStatus.ResetRequested), ct);
        if (!usable || unsafeProfile)
            return MaerskResumeOutcome.ProfileNotVerified;

        // One queued/running Maersk job per authorized resume; no mass replay.
        var alreadyActive = await db.AgentExecutions.AsNoTracking()
            .AnyAsync(e => e.ProviderId == execution.ProviderId
                && e.Id != executionId
                && (e.Status == AgentExecutionStatus.Queued
                    || e.Status == AgentExecutionStatus.Running), ct);
        if (alreadyActive)
            return MaerskResumeOutcome.ProviderBusy;

        execution.ResumeAfterVerification(actorId);
        db.MaerskCircuitEvents.Add(new MaerskCircuitEventRecord
        {
            Id = Guid.NewGuid(),
            ProviderId = execution.ProviderId,
            ExecutionId = execution.Id,
            EventType = "OperatorResume",
            ReasonCode = "resume_" + incident.Id.ToString("N"),
            ActorId = actorId,
            OperatorReason = reason.Trim(),
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return MaerskResumeOutcome.Resumed;
    }
}
