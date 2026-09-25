using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Application.ExtractionProfiles;
using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Dhole.Agent.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dhole.Agent.IntegrationTests;

[TestClass]
public sealed class ExecutionPipelineIntegrationTests
{
    [TestMethod]
    public async Task Execution_ShouldCompletePersistResultAndPublishOutboxEvents()
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        var provider = AgentProvider.Create(
            "MAERSK",
            "Maersk",
            AgentProviderType.Maersk,
            "https://www.maersk.com",
            AgentExecutionStrategy.BrowserNetworkCapture,
            true,
            null);

        db.AgentProviders.Add(provider);
        await db.SaveChangesAsync();

        var definition = AgentDefinition.Create(
            provider.Id,
            "MAERSK_SEARCH_OCEAN_RATES",
            "Maersk Ocean Freight Rate Search",
            null,
            AgentActionType.SearchOceanRates,
            AgentExecutionStrategy.BrowserNetworkCapture,
            null);

        db.AgentDefinitions.Add(definition);

        var profile = AgentExtractionProfile.Create(
            provider.Id,
            null,
            "Test profile",
            null,
            "https://www.maersk.com",
            null,
            "https://www.maersk.com",
            "Extract rates for {{providerCode}} from {{searchUrl}}.",
            AgentExecutionStrategy.BrowserNetworkCapture,
            null);

        db.AgentExtractionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var execution = AgentExecution.Create(
            definition.Id,
            provider.Id,
            null,
            null,
            AgentExecutionType.Manual,
            0,
            """{"pol":"Shanghai, China","pod":"Puerto Caldera, Costa Rica"}""",
            3,
            Guid.NewGuid().ToString("N"));

        execution.Queue();
        db.AgentExecutions.Add(execution);
        await db.SaveChangesAsync();

        var orchestrator = new AgentExecutionOrchestrator(
            new AgentExecutionRepository(db),
            new AgentDefinitionRepository(db),
            new AgentProviderRepository(db),
            new AgentCredentialRepository(db),
            new AgentScheduleRepository(db),
            new AgentExtractionProfileRepository(db),
            new AgentExtractionRouteRepository(db),
            new AgentExtractionEquipmentRepository(db),
            new AgentExtractionFieldRepository(db),
            new AgentEndpointCaptureRepository(db),
            new AgentExecutionSnapshotBuilder(new AgentPromptBuilder()),
            new AgentResultRepository(db),
            new FakeProviderResolver(),
            new TestUnitOfWork(db),
            NullLogger<AgentExecutionOrchestrator>.Instance);

        await orchestrator.ExecuteAsync(execution.Id);

        var storedExecution = await db.AgentExecutions.SingleAsync(x => x.Id == execution.Id);
        var storedResult = await db.AgentResults.SingleAsync(x => x.ExecutionId == execution.Id);
        var eventNames = await db.OutboxMessages.Select(x => x.EventName).ToListAsync();

        Assert.AreEqual(AgentExecutionStatus.Completed, storedExecution.Status);
        Assert.AreEqual(profile.Id, storedExecution.ExtractionProfileId);
        Assert.AreEqual(AgentResult.OceanFreightRates, storedResult.ResultType);
        Assert.AreEqual("""{"provider":"MAERSK","offers":[]}""", storedResult.DataJson);
        CollectionAssert.Contains(eventNames, "agent.execution.started");
        CollectionAssert.Contains(eventNames, "agent.execution.completed");
        CollectionAssert.Contains(eventNames, "agent.ocean-freight-rates.extracted");
    }

    private static ServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ServiceDbContext>()
            .UseInMemoryDatabase($"agent-pipeline-{Guid.NewGuid():N}")
            .Options;

        return new ServiceDbContext(options);
    }

    private sealed class TestUnitOfWork(ServiceDbContext dbContext) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => dbContext.SaveChangesAsync(cancellationToken);
    }

    private sealed class FakeProviderResolver : IAgentProviderResolver
    {
        private readonly IAgentProvider _provider = new FakeMaerskProvider();

        public IAgentProvider Resolve(string providerCode, AgentExecutionStrategy? executionStrategy = null)
        {
            Assert.AreEqual("MAERSK", providerCode);
            return _provider;
        }
    }

    private sealed class FakeMaerskProvider : IAgentProvider
    {
        public string ProviderCode => "MAERSK";

        public Task<AgentProviderExecutionResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken cancellationToken)
        {
            var result = AgentProviderExecutionResult.Completed(
                outputJson: """{"status":"ok"}""",
                resultType: AgentResult.OceanFreightRates,
                schemaVersion: "1.0",
                dataJson: """{"provider":"MAERSK","offers":[]}""");

            return Task.FromResult(result);
        }
    }
}
