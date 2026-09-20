using CustomCodeFramework.Auth.DependencyInjection;
using CustomCodeFramework.Core.Abstractions;
using CustomCodeFramework.Redis.DependencyInjection;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Infrastructure.Browser;
using Dhole.Agent.Infrastructure.Providers.Maersk;
using Dhole.Agent.Infrastructure.Providers.Maersk.Authentication;
using Dhole.Agent.Infrastructure.Providers.Maersk.Browser;
using Dhole.Agent.Infrastructure.Providers.Maersk.Network;
using Dhole.Agent.Infrastructure.Providers.Maersk.Parsers;
using Dhole.Agent.Infrastructure.Providers.Maersk.Resolvers;
using Dhole.Agent.Infrastructure.Runtime;
using Dhole.Agent.Infrastructure.Runtime.Hermes;
using Dhole.Agent.Infrastructure.Secrets;
using Dhole.Agent.Infrastructure.Time;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dhole.Agent.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddCustomCodeAuth(configuration);
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        });

        services.AddCustomCodeRedis(configuration);
        services.Configure<BrowserOptions>(configuration.GetSection(BrowserOptions.SectionName));
        services.AddSingleton<IBrowserProfileManager, BrowserProfileManager>();
        services.AddScoped<IBrowserManager, PlaywrightBrowserManager>();
        services.AddSingleton<ISecretProvider, EnvironmentSecretProvider>();
        services.AddSingleton<SecretRedactor>();

        services.AddScoped<MaerskLoginService>();
        services.AddScoped<MaerskBrowserAutomation>();
        services.AddScoped<MaerskOfferInterceptor>();
        services.AddScoped<MaerskOfferParser>();
        services.AddScoped<MaerskLocationResolver>();
        services.AddScoped<MaerskEquipmentResolver>();
        services.AddScoped<MaerskCommodityResolver>();
        services.AddScoped<IAgentProvider, MaerskAgentProvider>();

        services.Configure<HermesOptions>(configuration.GetSection(HermesOptions.SectionName));
        services.AddSingleton<HermesClient>();
        services.AddSingleton<IAgentRuntime, HermesAgentRuntime>();

        services.AddScoped<IAgentProviderResolver, AgentProviderResolver>();
        return services;
    }
}
