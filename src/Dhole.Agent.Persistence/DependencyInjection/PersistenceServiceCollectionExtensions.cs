using CustomCodeFramework.Postgres.DependencyInjection;
using CustomCodeFramework.Postgres.EntityFramework.DependencyInjection;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Persistence.DbContexts;
using Dhole.Agent.Persistence.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dhole.Agent.Persistence.DependencyInjection;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCustomCodePostgres(configuration);
        services.AddCustomCodePostgresEntityFramework<ServiceDbContext>();

        services.AddScoped<IAgentProviderRepository, AgentProviderRepository>();
        services.AddScoped<IAgentDefinitionRepository, AgentDefinitionRepository>();
        services.AddScoped<IAgentCredentialRepository, AgentCredentialRepository>();
        services.AddScoped<IAgentExtractionProfileRepository, AgentExtractionProfileRepository>();
        services.AddScoped<IAgentExtractionRouteRepository, AgentExtractionRouteRepository>();
        services.AddScoped<IAgentExtractionEquipmentRepository, AgentExtractionEquipmentRepository>();
        services.AddScoped<IAgentEndpointCaptureRepository, AgentEndpointCaptureRepository>();
        services.AddScoped<IBrowserProfileRepository, BrowserProfileRepository>();
        services.AddScoped<IAgentScheduleRepository, AgentScheduleRepository>();
        services.AddScoped<IAgentExecutionRepository, AgentExecutionRepository>();
        services.AddScoped<IAgentResultRepository, AgentResultRepository>();

        return services;
    }
}
