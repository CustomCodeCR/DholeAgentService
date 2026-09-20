using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dhole.Agent.Persistence.Seeding;

public static class AgentSeedDataExtensions
{
    public static async Task SeedAgentDataAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();

        await dbContext.Database.MigrateAsync(cancellationToken);

        var provider = await dbContext.AgentProviders
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Code == "MAERSK", cancellationToken);

        if (provider is null)
        {
            provider = AgentProvider.Create(
                code: "MAERSK",
                name: "Maersk",
                providerType: AgentProviderType.Maersk,
                baseUrl: "https://www.maersk.com",
                defaultExecutionStrategy: AgentExecutionStrategy.BrowserNetworkCapture,
                isSystem: true,
                metadataJson: null);

            dbContext.AgentProviders.Add(provider);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else if (!provider.IsActive)
        {
            provider.SetActive(true);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var definitionExists = await dbContext.AgentDefinitions
            .IgnoreQueryFilters()
            .AnyAsync(x => x.Code == "MAERSK_SEARCH_OCEAN_RATES", cancellationToken);

        if (!definitionExists)
        {
            dbContext.AgentDefinitions.Add(AgentDefinition.Create(
                providerId: provider.Id,
                code: "MAERSK_SEARCH_OCEAN_RATES",
                name: "Maersk Ocean Freight Rate Search",
                description: "Searches Maersk ocean freight offers using an authenticated browser session and deterministic network-response parsing.",
                actionType: AgentActionType.SearchOceanRates,
                executionStrategy: AgentExecutionStrategy.BrowserNetworkCapture,
                configurationJson: null));

            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
