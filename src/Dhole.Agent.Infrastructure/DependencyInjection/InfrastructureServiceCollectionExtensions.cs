using CustomCodeFramework.Auth.DependencyInjection;
using CustomCodeFramework.Redis.DependencyInjection;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Infrastructure.Browser;
using Dhole.Agent.Infrastructure.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dhole.Agent.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
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
        services.AddScoped<IAgentProviderResolver, AgentProviderResolver>();
        return services;
    }
}
