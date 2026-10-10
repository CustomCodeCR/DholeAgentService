using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Dhole.Agent.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.IntegrationTests;

[TestClass]
public sealed class MaerskMonitoringPostgresIntegrationTests
{
    [TestMethod]
    public async Task MultipleWorkersDeduplicateAlerts_AckPersists_AndRecoveryDoesNotResetCircuit()
    {
        var configured = Environment.GetEnvironmentVariable("AGENT_QUEUE_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Inconclusive("PostgreSQL is required to test alert upsert concurrency.");
            return;
        }
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(configured)
        {
            Database = "agent_monitor_test_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        await using var setup = CreateDb(connection);
        await setup.Database.MigrateAsync();
        try
        {
            var provider = AgentProvider.Create("MAERSK", "Maersk",
                AgentProviderType.Maersk, "https://www.maersk.com",
                AgentExecutionStrategy.Browser, true, null);
            setup.AgentProviders.Add(provider);
            await setup.SaveChangesAsync();
            await setup.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO agent.maersk_circuits (provider_id,state,requires_operator,reason_code) VALUES ({provider.Id}, 'Open', true, 'maersk_hcaptcha_required')");

            await using var worker1 = CreateDb(connection);
            await using var worker2 = CreateDb(connection);
            await Task.WhenAll(CreateMonitor(worker1).EvaluateAsync(),
                CreateMonitor(worker2).EvaluateAsync());

            var rows = await setup.MaerskHealthAlerts.AsNoTracking()
                .Where(x => x.ProviderId == provider.Id).ToListAsync();
            Assert.AreEqual(1, rows.Count, "One durable alert per provider/key.");
            var original = rows.Single();
            Assert.AreEqual(MaerskMonitoringPolicy.ProviderAccess, original.AlertKey);
            Assert.AreEqual("Active", original.State);
            Assert.AreEqual(1, original.Occurrences);

            var actor = Guid.NewGuid();
            Assert.IsTrue(await CreateMonitor(worker1)
                .AcknowledgeAsync(provider.Id, original.Id, actor));
            Assert.IsFalse(await CreateMonitor(worker2)
                .AcknowledgeAsync(provider.Id, original.Id, Guid.NewGuid()));
            Assert.IsTrue((await setup.MaerskHealthAlerts.AsNoTracking()
                .SingleAsync(x => x.Id == original.Id)).AcknowledgedAtUtc.HasValue);

            // No browser repair, IP rotation or Maersk circuit reset is triggered.
            Assert.AreEqual("Open", (await setup.MaerskCircuits.AsNoTracking()
                .SingleAsync(x => x.ProviderId == provider.Id)).State);

            await setup.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE agent.maersk_circuits SET state = 'Closed', requires_operator = false WHERE provider_id = {provider.Id}");
            await CreateMonitor(worker1).EvaluateAsync();
            var recovered = await setup.MaerskHealthAlerts.AsNoTracking()
                .SingleAsync(x => x.Id == original.Id);
            Assert.AreEqual("Resolved", recovered.State);
            Assert.IsNotNull(recovered.ResolvedAtUtc);

            await setup.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE agent.maersk_circuits SET state = 'Open', requires_operator = true WHERE provider_id = {provider.Id}");
            await CreateMonitor(worker2).EvaluateAsync();
            var reappeared = await setup.MaerskHealthAlerts.AsNoTracking()
                .SingleAsync(x => x.Id == original.Id);
            Assert.AreEqual(original.Id, reappeared.Id);
            Assert.AreEqual("Active", reappeared.State);
            Assert.AreEqual(2, reappeared.Occurrences);
            Assert.IsNull(reappeared.AcknowledgedAtUtc);
        }
        finally { await setup.Database.EnsureDeletedAsync(); }
    }

    private static ServiceDbContext CreateDb(string connection)
        => new(new DbContextOptionsBuilder<ServiceDbContext>().UseNpgsql(connection).Options);

    private static PostgresMaerskMonitoring CreateMonitor(ServiceDbContext db)
        => new(db, new AgentProviderRepository(db),
            Options.Create(new MaerskMonitoringOptions { Enabled = true }),
            Options.Create(new MaerskCircuitOptions { Enabled = true }),
            NullLogger<PostgresMaerskMonitoring>.Instance);
}
