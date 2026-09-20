using Dhole.Agent.Application.Abstractions.Runtime;

namespace Dhole.Agent.Infrastructure.Runtime;

public sealed class AgentProviderResolver(IEnumerable<IAgentProvider> providers):IAgentProviderResolver
{
    private readonly IReadOnlyDictionary<string,IAgentProvider> _providers=providers.ToDictionary(x=>x.ProviderCode,StringComparer.OrdinalIgnoreCase);

    public IAgentProvider Resolve(string providerCode)
        => _providers.TryGetValue(providerCode,out var provider)
            ? provider
            : throw new InvalidOperationException($"No agent provider is registered for '{providerCode}'.");
}
