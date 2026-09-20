using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Dhole.Agent.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.IntegrationTests;

[TestClass]
public sealed class PersistenceIntegrationTests
{
    [TestMethod]
    public async Task ServiceDbContext_ShouldExposeAgentModelInboxAndOutbox()
    {
        await using var db = CreateDbContext();

        await db.Database.EnsureCreatedAsync();

        var entityNames = db.Model.GetEntityTypes().Select(x => x.ClrType.Name).ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(entityNames.Contains(nameof(AgentProvider)));
        Assert.IsTrue(entityNames.Contains(nameof(AgentExecution)));
        Assert.IsTrue(entityNames.Contains("OutboxMessage"));
        Assert.IsTrue(entityNames.Contains("InboxMessage"));
    }

    [TestMethod]
    public void PersistenceAssembly_ShouldContainInitialAgentSchemaMigration()
    {
        var migrationExists = typeof(ServiceDbContext).Assembly
            .GetTypes()
            .Any(x => string.Equals(x.Name, "InitialAgentSchema", StringComparison.Ordinal));

        Assert.IsTrue(migrationExists);
    }

    [TestMethod]
    public async Task SaveChanges_ShouldConvertDomainEventToOutbox()
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        db.AgentProviders.Add(AgentProvider.Create(
            "MAERSK",
            "Maersk",
            AgentProviderType.Maersk,
            "https://www.maersk.com",
            AgentExecutionStrategy.BrowserNetworkCapture,
            true,
            null));

        await db.SaveChangesAsync();

        var message = await db.OutboxMessages.SingleAsync();
        Assert.AreEqual("agent.provider.created", message.EventName);
        Assert.AreEqual("DholeAgentService", message.SourceService);
        Assert.IsFalse(string.IsNullOrWhiteSpace(message.PayloadJson));
    }

    [TestMethod]
    public async Task AgentProviderRepository_ShouldFindByNormalizedCode()
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        var repository = new AgentProviderRepository(db);
        var provider = AgentProvider.Create(
            "MAERSK",
            "Maersk",
            AgentProviderType.Maersk,
            "https://www.maersk.com",
            AgentExecutionStrategy.BrowserNetworkCapture,
            true,
            null);

        await repository.AddAsync(provider);
        await db.SaveChangesAsync();

        var loaded = await repository.GetByCodeAsync(" maersk ");

        Assert.IsNotNull(loaded);
        Assert.AreEqual(provider.Id, loaded.Id);
    }

    private static ServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ServiceDbContext>()
            .UseInMemoryDatabase($"agent-tests-{Guid.NewGuid():N}")
            .Options;

        return new ServiceDbContext(options);
    }
}
