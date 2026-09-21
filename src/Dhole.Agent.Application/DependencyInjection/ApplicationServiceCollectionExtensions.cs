using CustomCodeFramework.Cqrs.DependencyInjection;
using CustomCodeFramework.Validation.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Application.Runtime;
namespace Dhole.Agent.Application.DependencyInjection;
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddCustomCodeValidation(AssemblyReference.Assembly);
        services.AddCustomCodeCqrs(AssemblyReference.Assembly);
        services.AddCustomCodeCqrsBehaviors();
        services.AddScoped<IAgentExecutionOrchestrator, AgentExecutionOrchestrator>();
        services.AddSingleton<Dhole.Agent.Application.ExtractionProfiles.EndpointCaptureMatcher>();
        return services;
    }
}
