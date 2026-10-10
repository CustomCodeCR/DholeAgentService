using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Dhole.Agent.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.IntegrationTests;

[TestClass]
public sealed class MaerskCircuitPostgresIntegrationTests
{
    private const string Variable = "AGENT_QUEUE_POSTGRES_TEST_CONNECTION";

    [TestMethod]
    public async Task CaptchaAnd429_StayOpenEvenAfterCooldownUntilVerifiedOperatorReset()
    {
        var connection = ConnectionOrInconclusive();
        if (connection is null) return;
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();
        try
        {
            var providerId = await SeedProviderAsync(db);
            var circuit = CreateCircuit(db);
            var executionId = Guid.NewGuid();

            Assert.IsTrue(await circuit.TryEnterAsync(providerId, executionId));
            await circuit.RecordFailureAsync(providerId, executionId, "maersk_hcaptcha_required");

            var state = await circuit.GetAsync(providerId);
            Assert.AreEqual("Open", state.State);
            Assert.IsTrue(state.RequiresOperator);
            Assert.AreEqual("maersk_hcaptcha_required", state.ReasonCode);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE agent.maersk_circuits SET open_until_utc = NOW() - INTERVAL '1 second' WHERE provider_id = {providerId}");

            Assert.IsFalse(await circuit.CanScheduleAsync(providerId));
            Assert.IsFalse(await circuit.TryEnterAsync(providerId, Guid.NewGuid()));
            Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await circuit.ResetByOperatorAsync(providerId, Guid.NewGuid(),
                    "Operator did not verify", false));
            Assert.IsFalse(await circuit.ResetByOperatorAsync(providerId, Guid.NewGuid(),
                "Provider verification complete", true) == false);
            Assert.AreEqual("Closed", (await circuit.GetAsync(providerId)).State);

            var next = Guid.NewGuid();
            await circuit.RecordFailureAsync(providerId, next, "maersk_authentication_rate_limited");
            Assert.IsFalse(await circuit.CanScheduleAsync(providerId));
            Assert.IsTrue((await circuit.GetAsync(providerId)).RequiresOperator);

            var events = await db.Database.SqlQueryRaw<long>(
                "SELECT COUNT(*) AS \"Value\" FROM agent.maersk_circuit_events WHERE provider_id = {0}",
                providerId).SingleAsync();
            Assert.IsTrue(events >= 3, "Opening and verified reset must be audited.");
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [TestMethod]
    public async Task TechnicalThreshold_AllowsOnlyOneHalfOpenProbeAcrossWorkers()
    {
        var connection = ConnectionOrInconclusive();
        if (connection is null) return;
        await using var setup = CreateDb(connection);
        await setup.Database.MigrateAsync();
        try
        {
            var providerId = await SeedProviderAsync(setup);
            var circuit = CreateCircuit(setup);
            for (var i = 0; i < 3; i++)
                await circuit.RecordFailureAsync(providerId, Guid.NewGuid(), "maersk_offer_timeout");

            var state = await circuit.GetAsync(providerId);
            Assert.AreEqual("Open", state.State);
            Assert.IsFalse(state.RequiresOperator);
            Assert.AreEqual(3, state.ConsecutiveFailures);
            Assert.IsFalse(await circuit.CanScheduleAsync(providerId));

            // Simulate completed technical cooldown without a slow integration test.
            await setup.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE agent.maersk_circuits SET open_until_utc = NOW() - INTERVAL '1 second' WHERE provider_id = {providerId}");

            await using var workerA = CreateDb(connection);
            await using var workerB = CreateDb(connection);
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var claimed = await Task.WhenAll(
                CreateCircuit(workerA).TryEnterAsync(providerId, a),
                CreateCircuit(workerB).TryEnterAsync(providerId, b));

            Assert.AreEqual(1, claimed.Count(x => x),
                "Half-open must permit exactly one diagnostic run.");
            var winner = claimed[0] ? a : b;
            Assert.AreEqual("HalfOpen", (await circuit.GetAsync(providerId)).State);
            Assert.IsFalse(await circuit.CanScheduleAsync(providerId));
            await circuit.RecordSuccessAsync(providerId, claimed[0] ? b : a);
            Assert.AreEqual("HalfOpen", (await circuit.GetAsync(providerId)).State,
                "A non-owner cannot close a half-open circuit.");
            await circuit.RecordSuccessAsync(providerId, winner);
            Assert.AreEqual("Closed", (await circuit.GetAsync(providerId)).State);
        }
        finally { await setup.Database.EnsureDeletedAsync(); }
    }

    [TestMethod]
    public async Task FailedProbe_ReopensForCooldownWithoutRotatingProviderIdentity()
    {
        var connection = ConnectionOrInconclusive();
        if (connection is null) return;
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();
        try
        {
            var providerId = await SeedProviderAsync(db);
            var circuit = CreateCircuit(db);
            for (var i = 0; i < 3; i++)
                await circuit.RecordFailureAsync(providerId, Guid.NewGuid(), "maersk_offer_timeout");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE agent.maersk_circuits SET open_until_utc = NOW() - INTERVAL '1 second' WHERE provider_id = {providerId}");

            var probeId = Guid.NewGuid();
            Assert.IsTrue(await circuit.TryEnterAsync(providerId, probeId));
            await circuit.RecordFailureAsync(providerId, probeId, "maersk_offer_timeout");
            var state = await circuit.GetAsync(providerId);
            Assert.AreEqual("Open", state.State);
            Assert.IsFalse(state.RequiresOperator);
            Assert.IsTrue(state.OpenUntilUtc > DateTime.UtcNow);
            Assert.IsFalse(await circuit.CanScheduleAsync(providerId));
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private static string? ConnectionOrInconclusive()
    {
        var value = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            Assert.Inconclusive("PostgreSQL connection required to test circuit atomics.");
            return null;
        }
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(value)
        {
            Database = "agent_circuit_test_" + Guid.NewGuid().ToString("N")
        };
        return builder.ConnectionString;
    }

    private static ServiceDbContext CreateDb(string connection)
        => new(new DbContextOptionsBuilder<ServiceDbContext>().UseNpgsql(connection).Options);

    private static PostgresMaerskCircuitBreaker CreateCircuit(ServiceDbContext db)
        => new(db, Options.Create(new MaerskCircuitOptions
        {
            Enabled = true,
            TransientFailureThreshold = 3,
            TechnicalCooldownSeconds = 300,
            ProviderCooldownSeconds = 900
        }));

    private static async Task<Guid> SeedProviderAsync(ServiceDbContext db)
    {
        var provider = AgentProvider.Create(
            "MAERSK", "Maersk", AgentProviderType.Maersk,
            "https://www.maersk.com", AgentExecutionStrategy.BrowserNetworkCapture,
            true, null);
        db.AgentProviders.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id;
    }
}
