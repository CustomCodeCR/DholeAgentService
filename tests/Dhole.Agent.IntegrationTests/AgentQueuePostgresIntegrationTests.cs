using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Dhole.Agent.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.IntegrationTests;

[TestClass]
public sealed class AgentQueuePostgresIntegrationTests
{
    private const string ConnectionVariable = "AGENT_QUEUE_POSTGRES_TEST_CONNECTION";

    [TestMethod]
    public async Task CompetingWorkers_ClaimOnlyOnceAndEnforceOwnerHeartbeat()
    {
        var connection = ConnectionStringOrInconclusive();
        if (connection is null) return;
        await using var setup = CreateDb(connection);
        await setup.Database.MigrateAsync();
        try
        {
            var execution = await SeedQueuedAsync(setup, "MAERSK", AgentExecutionType.Scheduled);
            var leaseScope = $"maersk:{execution.ProviderId:N}";
            var owner1 = Guid.NewGuid();
            var owner2 = Guid.NewGuid();

            await using var worker1 = CreateDb(connection);
            await using var worker2 = CreateDb(connection);
            var claim1 = new PostgresAgentQueueLeaseStore(worker1);
            var claim2 = new PostgresAgentQueueLeaseStore(worker2);

            var claimed = await Task.WhenAll(
                claim1.TryClaimAsync(execution.Id, leaseScope, owner1, TimeSpan.FromSeconds(180)),
                claim2.TryClaimAsync(execution.Id, leaseScope, owner2, TimeSpan.FromSeconds(180)));

            Assert.AreEqual(1, claimed.Count(x => x), "Only one PostgreSQL owner may claim a job.");
            var winnerStore = claimed[0] ? claim1 : claim2;
            var loserStore = claimed[0] ? claim2 : claim1;
            var winner = claimed[0] ? owner1 : owner2;
            var loser = claimed[0] ? owner2 : owner1;

            Assert.IsFalse(await loserStore.RenewAsync(
                leaseScope, execution.Id, loser, TimeSpan.FromSeconds(180)));
            Assert.IsTrue(await winnerStore.RenewAsync(
                leaseScope, execution.Id, winner, TimeSpan.FromSeconds(180)));

            // A duplicate Redis message is only a notification. A repeated
            // PostgreSQL claim cannot dispatch the same execution.
            Assert.IsFalse(await loserStore.TryClaimAsync(
                execution.Id, leaseScope, loser, TimeSpan.FromSeconds(180)));

            await loserStore.ReleaseAsync(leaseScope, execution.Id, loser);
            Assert.IsTrue(await winnerStore.RenewAsync(
                leaseScope, execution.Id, winner, TimeSpan.FromSeconds(180)));
            await winnerStore.ReleaseAsync(leaseScope, execution.Id, winner);
        }
        finally { await setup.Database.EnsureDeletedAsync(); }
    }

    [TestMethod]
    public async Task ExpiredRunningLease_BecomesInterruptedWithoutReplaying()
    {
        var connection = ConnectionStringOrInconclusive();
        if (connection is null) return;
        await using var setup = CreateDb(connection);
        await setup.Database.MigrateAsync();
        try
        {
            var execution = await SeedQueuedAsync(setup, "MAERSK", AgentExecutionType.Scheduled);
            var store = new PostgresAgentQueueLeaseStore(setup);
            var key = $"maersk:{execution.ProviderId:N}";
            var owner = Guid.NewGuid();

            Assert.IsTrue(await store.TryClaimAsync(
                execution.Id, key, owner, TimeSpan.FromSeconds(180)));
            execution.Start(DateTime.UtcNow);
            await setup.SaveChangesAsync();
            await setup.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE agent.execution_leases SET expires_at_utc = NOW() - INTERVAL '1 second' WHERE execution_id = {execution.Id}");

            Assert.AreEqual(1, await store.FailInterruptedAsync());
            var result = await setup.AgentExecutions.AsNoTracking()
                .SingleAsync(x => x.Id == execution.Id);
            Assert.AreEqual(AgentExecutionStatus.Failed, result.Status);
            Assert.AreEqual("agent_worker_interrupted", result.ErrorCode);
            Assert.IsFalse(await store.TryClaimAsync(
                execution.Id, key, Guid.NewGuid(), TimeSpan.FromSeconds(180)));
        }
        finally { await setup.Database.EnsureDeletedAsync(); }
    }

    [TestMethod]
    public async Task LargeMaerskBacklog_DoesNotHideOtherProvidersOrCron()
    {
        await using var db = new ServiceDbContext(
            new DbContextOptionsBuilder<ServiceDbContext>()
                .UseInMemoryDatabase($"queue-fairness-{Guid.NewGuid():N}")
                .Options);
        await db.Database.EnsureCreatedAsync();
        var providerMaersk = AgentProvider.Create(
            "MAERSK", "Maersk", AgentProviderType.Maersk,
            "https://www.maersk.com", AgentExecutionStrategy.Browser, true, null);
        var providerOther = AgentProvider.Create(
            "OTHER", "Other", AgentProviderType.GenericWeb,
            "https://example.invalid", AgentExecutionStrategy.Hermes, false, null);
        db.AgentProviders.AddRange(providerMaersk, providerOther);
        await db.SaveChangesAsync();

        var maerskDefinition = AgentDefinition.Create(
            providerMaersk.Id, "MAERSK_TEST", "Maersk test", null,
            AgentActionType.SearchOceanRates, AgentExecutionStrategy.Browser, null);
        var otherDefinition = AgentDefinition.Create(
            providerOther.Id, "OTHER_TEST", "Other test", null,
            AgentActionType.GenericExtraction, AgentExecutionStrategy.Hermes, null);
        db.AgentDefinitions.AddRange(maerskDefinition, otherDefinition);

        for (var i = 0; i < 60; i++)
        {
            var execution = AgentExecution.Create(
                maerskDefinition.Id, providerMaersk.Id, null, null,
                AgentExecutionType.Manual, 100, "{}", 2, Guid.NewGuid().ToString("N"));
            execution.Queue();
            db.AgentExecutions.Add(execution);
        }

        var cron = AgentExecution.Create(
            otherDefinition.Id, providerOther.Id, null, null,
            AgentExecutionType.Scheduled, 0, "{}", 2, Guid.NewGuid().ToString("N"));
        cron.Queue();
        db.AgentExecutions.Add(cron);
        await db.SaveChangesAsync();

        var candidates = await new AgentExecutionRepository(db)
            .GetDispatchCandidatesAsync(DateTime.UtcNow.AddMinutes(1), 16);

        Assert.IsTrue(candidates.Count <= 16);
        Assert.IsTrue(candidates.Any(x => x.Id == cron.Id),
            "A large Maersk backlog must not hide cron tasks from other providers.");
    }

    [TestMethod]
    public async Task WaitingForAuthentication_IsNotClaimableAndDoesNotHideOtherProviders()
    {
        var connection = ConnectionStringOrInconclusive();
        if (connection is null) return;
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();
        try
        {
            var waiting = await SeedQueuedAsync(db, "MAERSK", AgentExecutionType.Scheduled);
            waiting.Start(DateTime.UtcNow);
            waiting.WaitForAuthentication("maersk_hcaptcha_required", "Provider verification required.");
            var unrelated = await SeedQueuedAsync(db, "OTHER", AgentExecutionType.Manual);
            await db.SaveChangesAsync();

            var candidates = await new AgentExecutionRepository(db)
                .GetDispatchCandidatesAsync(DateTime.UtcNow.AddMinutes(1), 4);
            Assert.IsFalse(candidates.Any(x => x.Id == waiting.Id),
                "Provider verification must remove the execution from active polling.");
            Assert.IsTrue(candidates.Any(x => x.Id == unrelated.Id),
                "An unrelated queued provider should continue to be visible.");

            var store = new PostgresAgentQueueLeaseStore(db);
            Assert.IsFalse(await store.TryClaimAsync(waiting.Id,
                $"maersk:{waiting.ProviderId:N}", Guid.NewGuid(), TimeSpan.FromSeconds(180)));
            Assert.IsTrue(await store.TryClaimAsync(unrelated.Id,
                $"execution:{unrelated.Id:N}", Guid.NewGuid(), TimeSpan.FromSeconds(180)));
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [TestMethod]
    public async Task OneMaerskProviderLease_DoesNotBlockUnrelatedProviderOnAnotherWorker()
    {
        var connection = ConnectionStringOrInconclusive();
        if (connection is null) return;
        await using var setup = CreateDb(connection);
        await setup.Database.MigrateAsync();
        try
        {
            var first = await SeedQueuedAsync(setup, "MAERSK", AgentExecutionType.Scheduled);
            var second = AgentExecution.Create(first.AgentDefinitionId, first.ProviderId,
                null, null, AgentExecutionType.Manual, 100, "{}", 2,
                Guid.NewGuid().ToString("N"));
            second.Queue();
            setup.AgentExecutions.Add(second);
            var unrelated = await SeedQueuedAsync(setup, "OTHER", AgentExecutionType.Manual);
            await setup.SaveChangesAsync();

            await using var workerA = CreateDb(connection);
            await using var workerB = CreateDb(connection);
            var storeA = new PostgresAgentQueueLeaseStore(workerA);
            var storeB = new PostgresAgentQueueLeaseStore(workerB);
            var maerskScope = $"maersk:{first.ProviderId:N}";
            var maerskOwner = Guid.NewGuid();
            Assert.IsTrue(await storeA.TryClaimAsync(first.Id, maerskScope,
                maerskOwner, TimeSpan.FromSeconds(180)));

            Assert.IsFalse(await storeB.TryClaimAsync(second.Id, maerskScope,
                Guid.NewGuid(), TimeSpan.FromSeconds(180)),
                "A second Maersk job must not use the same provider session in parallel.");
            var otherScope = $"execution:{unrelated.Id:N}";
            var otherOwner = Guid.NewGuid();
            Assert.IsTrue(await storeB.TryClaimAsync(unrelated.Id, otherScope,
                otherOwner, TimeSpan.FromSeconds(180)),
                "Maersk's busy session must not block other providers.");
            await storeA.ReleaseAsync(maerskScope, first.Id, maerskOwner);
            await storeB.ReleaseAsync(otherScope, unrelated.Id, otherOwner);
        }
        finally { await setup.Database.EnsureDeletedAsync(); }
    }

    [TestMethod]
    public async Task LegacyRunningWithoutLease_ExpiresAsFailedAndNeverReplaysAutomatically()
    {
        var connection = ConnectionStringOrInconclusive();
        if (connection is null) return;
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();
        try
        {
            var interrupted = await SeedQueuedAsync(db, "MAERSK", AgentExecutionType.Manual);
            interrupted.Start(DateTime.UtcNow.AddHours(-13));
            await db.SaveChangesAsync();
            var store = new PostgresAgentQueueLeaseStore(db);

            Assert.AreEqual(1, await store.FailInterruptedAsync());
            var state = await db.AgentExecutions.AsNoTracking()
                .SingleAsync(x => x.Id == interrupted.Id);
            Assert.AreEqual(AgentExecutionStatus.Failed, state.Status);
            Assert.AreEqual("agent_legacy_execution_interrupted", state.ErrorCode);
            Assert.IsFalse(await store.TryClaimAsync(interrupted.Id,
                $"maersk:{interrupted.ProviderId:N}", Guid.NewGuid(),
                TimeSpan.FromSeconds(180)));
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private static string? ConnectionStringOrInconclusive()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Inconclusive($"Set {ConnectionVariable} to run the real PostgreSQL lease tests.");
            return null;
        }
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(configured)
        {
            Database = "agent_queue_test_" + Guid.NewGuid().ToString("N")
        };
        return builder.ConnectionString;
    }

    private static ServiceDbContext CreateDb(string connection)
        => new(new DbContextOptionsBuilder<ServiceDbContext>().UseNpgsql(connection).Options);

    private static async Task<AgentExecution> SeedQueuedAsync(
        ServiceDbContext db, string providerCode, AgentExecutionType type)
    {
        var provider = AgentProvider.Create(
            providerCode, providerCode, AgentProviderType.Maersk,
            "https://www.maersk.com", AgentExecutionStrategy.BrowserNetworkCapture,
            true, null);
        db.AgentProviders.Add(provider);
        var definition = AgentDefinition.Create(
            provider.Id, "TEST_SEARCH", "Test", null,
            AgentActionType.SearchOceanRates,
            AgentExecutionStrategy.BrowserNetworkCapture, null);
        db.AgentDefinitions.Add(definition);
        var execution = AgentExecution.Create(
            definition.Id, provider.Id, null, null, type, 0,
            """{"test":true}""", 3, Guid.NewGuid().ToString("N"));
        execution.Queue();
        db.AgentExecutions.Add(execution);
        await db.SaveChangesAsync();
        return execution;
    }
}
