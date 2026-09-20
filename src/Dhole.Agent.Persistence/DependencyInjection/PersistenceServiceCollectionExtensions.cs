using Dhole.Agent.Persistence.DbContexts;
using CustomCodeFramework.Postgres.EntityFramework.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dhole.Agent.Persistence.DependencyInjection;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCustomCodePostgresEntityFramework<ServiceDbContext>();
        return services;
    }
}
