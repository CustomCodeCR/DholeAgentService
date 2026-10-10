using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Dhole.Agent.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.IntegrationTests;

/// <summary>
/// Offline phase-7 acceptance scenarios using an isolated PostgreSQL 16 database.
/// Never contacts Maersk, logs in, regenerates browser identities or sends traffic.
/// </summary>
[TestClass]
public sealed class MaerskPhase7AcceptancePostgresTests
{
    private const string ConnectionVariable = "AGENT_QUEUE_POSTGRES_TEST_CONNECTION";

    [TestMethod]
    public async Task CaptchaPausesOriginalJob_WithoutStarvingOtherProvider()
    {
        var connection = IsolatedConnection();
        if (connection is null) return;
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();
        try
        {
            var maersk = await SeedQueuedAsync(db, "MAERSK", AgentProviderType.Maersk);
            var other = await SeedQueuedAsync(db, "OTHER", AgentProviderType.GenericWeb);
            var originalInput = maersk.Execution.InputJson;
            var originalCorrelation = maersk.Execution.CorrelationId;
            var originalId = maersk.Execution.Id;

            maersk.Execution.Start(DateTime.UtcNow);
            maersk.Execution.WaitForAuthentication("maersk_hcaptcha_required",
                "Provider verification required");
            await db.SaveChangesAsync();
            var circuit = Circuit(db);
            await circuit.RecordFailureAsync(maersk.Provider.Id, originalId, "maersk_hcaptcha_required");

            Assert.AreEqual("Open", (await circuit.GetAsync(maersk.Provider.Id)).State);
            Assert.IsTrue((await circuit.GetAsync(maersk.Provider.Id)).RequiresOperator);
            Assert.IsFalse(await circuit.CanScheduleAsync(maersk.Provider.Id));

            var lease = new PostgresAgentQueueLeaseStore(db);
            Assert.IsFalse(await lease.TryClaimAsync(originalId,
                $"maersk:{maersk.Provider.Id:N}", Guid.NewGuid(), TimeSpan.FromSeconds(180)));
            // The provider-wide gate is only for Maersk: other providers stay operational.
            Assert.IsTrue(await lease.TryClaimAsync(other.Execution.Id,
                $"execution:{other.Execution.Id:N}", Guid.NewGuid(), TimeSpan.FromSeconds(180)));

            var stored = await db.AgentExecutions.AsNoTracking()
                .SingleAsync(x => x.Id == originalId);
            Assert.AreEqual(AgentExecutionStatus.WaitingForAuthentication, stored.Status);
            Assert.AreEqual(originalId, stored.Id);
            Assert.AreEqual(originalInput, stored.InputJson);
            Assert.AreEqual(originalCorrelation, stored.CorrelationId);
            Assert.AreEqual(1, stored.Attempt);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [TestMethod]
    public async Task VerifiedResetCannotClearCircuitWhileRunningLeaseIsActive()
    {
        var connection = IsolatedConnection();
        if (connection is null) return;
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();
        try
        {
            var seeded = await SeedQueuedAsync(db, "MAERSK", AgentProviderType.Maersk);
            var breaker = Circuit(db);
            await breaker.RecordFailureAsync(seeded.Provider.Id, seeded.Execution.Id,
                "maersk_authentication_rate_limited");

            var lease = new PostgresAgentQueueLeaseStore(db);
            var leaseKey = $"maersk:{seeded.Provider.Id:N}";
            var owner = Guid.NewGuid();
            Assert.IsTrue(await lease.TryClaimAsync(seeded.Execution.Id, leaseKey,
                owner, TimeSpan.FromSeconds(180)));
            seeded.Execution.Start(DateTime.UtcNow);
            await db.SaveChangesAsync();

            var actor = Guid.NewGuid();
            Assert.IsFalse(await breaker.ResetByOperatorAsync(seeded.Provider.Id,
                actor, "Provider verification completed by operator", true));
            Assert.IsTrue((await breaker.GetAsync(seeded.Provider.Id)).RequiresOperator);

            // Ending work is necessary but not sufficient: explicit operator verification
            // is still required, and release cannot be automatic.
            seeded.Execution.WaitForAuthentication("maersk_authentication_rate_limited",
                "Operator review still required");
            await db.SaveChangesAsync();
            Assert.IsFalse(await breaker.ResetByOperatorAsync(seeded.Provider.Id,
                actor, "No real provider verification", false));
            Assert.IsTrue((await breaker.GetAsync(seeded.Provider.Id)).RequiresOperator);

            Assert.IsTrue(await breaker.ResetByOperatorAsync(seeded.Provider.Id,
                actor, "Legitimate access independently confirmed", true));
            Assert.AreEqual("Closed", (await breaker.GetAsync(seeded.Provider.Id)).State);
            Assert.AreEqual(AgentExecutionStatus.WaitingForAuthentication,
                (await db.AgentExecutions.AsNoTracking().SingleAsync(x => x.Id == seeded.Execution.Id)).Status);

            var resets = await db.MaerskCircuitEvents.AsNoTracking()
                .CountAsync(x => x.ProviderId == seeded.Provider.Id && x.EventType == "OperatorReset");
            Assert.AreEqual(1, resets);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [TestMethod]
    public async Task TechnicalRetryRespectsBackoff_AndPreservesOriginalSnapshotAcrossOwners()
    {
        var connection = IsolatedConnection();
        if (connection is null) return;
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();
        try
        {
            var seeded = await SeedQueuedAsync(db, "MAERSK", AgentProviderType.Maersk);
            var execution = seeded.Execution;
            var profile = AgentExtractionProfile.Create(
                seeded.Provider.Id, null, "Acceptance snapshot profile", null,
                "https://example.invalid", null, null, "https://example.invalid",
                "Offline test prompt", AgentExecutionStrategy.Browser, null);
            db.AgentExtractionProfiles.Add(profile);
            await db.SaveChangesAsync();
            var snapshotId = profile.Id;
            execution.AttachProfileSnapshot(snapshotId,
                "Frozen snapshot for one execution", """{"source":"original"}""");
            execution.Start(DateTime.UtcNow);
            var originalId = execution.Id;
            var correlation = execution.CorrelationId;
            var input = execution.InputJson;
            execution.QueueTransientRetry("maersk_chromium_launch_failed",
                "Local browser technical failure", DateTime.UtcNow.AddMinutes(2));
            await db.SaveChangesAsync();

            var leaseKey = $"maersk:{seeded.Provider.Id:N}";
            var owner1 = Guid.NewGuid();
            var owner2 = Guid.NewGuid();
            await using var contender = CreateDb(connection);
            var first = new PostgresAgentQueueLeaseStore(db);
            var second = new PostgresAgentQueueLeaseStore(contender);
            Assert.IsFalse(await first.TryClaimAsync(originalId, leaseKey, owner1,
                TimeSpan.FromSeconds(180)), "Retry must wait for its due time.");

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE agent.\"AgentExecutions\" SET next_attempt_at_utc = NOW() - INTERVAL '1 second' WHERE id = {originalId}");

            var results = await Task.WhenAll(
                first.TryClaimAsync(originalId, leaseKey, owner1, TimeSpan.FromSeconds(180)),
                second.TryClaimAsync(originalId, leaseKey, owner2, TimeSpan.FromSeconds(180)));
            Assert.AreEqual(1, results.Count(x => x), "Exactly one worker owns the retry.");
            var persisted = await db.AgentExecutions.AsNoTracking().SingleAsync(x => x.Id == originalId);
            Assert.AreEqual(originalId, persisted.Id);
            Assert.AreEqual(correlation, persisted.CorrelationId);
            Assert.AreEqual(input, persisted.InputJson);
            Assert.AreEqual(snapshotId, persisted.ExtractionProfileId);
            Assert.AreEqual("Frozen snapshot for one execution", persisted.PromptSnapshot);
            Assert.AreEqual("""{"source":"original"}""", persisted.ConfigurationSnapshotJson);
            Assert.AreEqual(1, persisted.Attempt);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [TestMethod]
    public async Task DisabledMonitoringCannotAcknowledgeOrMutateExistingIncident()
    {
        var connection = IsolatedConnection();
        if (connection is null) return;
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();
        try
        {
            var seeded = await SeedQueuedAsync(db, "MAERSK", AgentProviderType.Maersk);
            var incidentId = Guid.NewGuid();
            db.MaerskHealthAlerts.Add(new MaerskHealthAlertRecord
            {
                Id = incidentId, ProviderId = seeded.Provider.Id, AlertKey = "provider-access",
                Code = "maersk_provider_verification_required", Severity = "Critical",
                State = "Active", Occurrences = 1,
                FirstSeenAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var monitoring = new PostgresMaerskMonitoring(db,
                new AgentProviderRepository(db),
                Options.Create(new MaerskMonitoringOptions { Enabled = false }),
                Options.Create(new MaerskCircuitOptions { Enabled = false }),
                NullLogger<PostgresMaerskMonitoring>.Instance);

            await monitoring.EvaluateAsync();
            Assert.IsFalse(await monitoring.AcknowledgeAsync(seeded.Provider.Id, incidentId,
                Guid.NewGuid()));
            var snapshot = await monitoring.GetSnapshotAsync(seeded.Provider.Id);
            Assert.IsFalse(snapshot.MonitoringEnabled);
            Assert.IsEmpty(snapshot.Alerts);
            var persisted = await db.MaerskHealthAlerts.AsNoTracking()
                .SingleAsync(x => x.Id == incidentId);
            Assert.AreEqual("Active", persisted.State);
            Assert.IsNull(persisted.AcknowledgedAtUtc);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private static string? IsolatedConnection()
    {
        var raw = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.Inconclusive("Real PostgreSQL 16 is mandatory for phase-7 acceptance.");
            return null;
        }
        return new Npgsql.NpgsqlConnectionStringBuilder(raw)
        {
            Database = "agent_phase7_test_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
    }

    private static ServiceDbContext CreateDb(string connection)
        => new(new DbContextOptionsBuilder<ServiceDbContext>().UseNpgsql(connection).Options);

    private static PostgresMaerskCircuitBreaker Circuit(ServiceDbContext db)
        => new(db, Options.Create(new MaerskCircuitOptions { Enabled = true }));

    private static async Task<(AgentProvider Provider, AgentExecution Execution)> SeedQueuedAsync(
        ServiceDbContext db, string providerCode, AgentProviderType providerType)
    {
        var provider = AgentProvider.Create(providerCode, providerCode,
            providerType, "https://example.invalid", AgentExecutionStrategy.Browser,
            false, null);
        var definition = AgentDefinition.Create(provider.Id,
            $"{providerCode}_SEARCH", "Acceptance test", null,
            AgentActionType.SearchOceanRates, AgentExecutionStrategy.Browser, null);
        var execution = AgentExecution.Create(definition.Id, provider.Id, null, null,
            AgentExecutionType.Scheduled, 0, """{"pol":"Shanghai","pod":"Caldera"}""",
            3, Guid.NewGuid().ToString("N"));
        execution.Queue();
        db.AgentProviders.Add(provider);
        db.AgentDefinitions.Add(definition);
        db.AgentExecutions.Add(execution);
        await db.SaveChangesAsync();
        return (provider, execution);
    }
}
