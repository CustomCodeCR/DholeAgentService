using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Infrastructure.Providers.Generic;

namespace Dhole.Agent.Infrastructure.Runtime;

public sealed class AgentProviderResolver : IAgentProviderResolver
{
    private readonly IReadOnlyDictionary<string, IAgentProvider> _providers;
    private readonly IAgentProvider? _fallback;

    public AgentProviderResolver(IEnumerable<IAgentProvider> providers)
    {
        var registered = providers.ToArray();
        _fallback = registered.FirstOrDefault(x =>
            string.Equals(
                x.ProviderCode,
                HermesGenericAgentProvider.FallbackProviderCode,
                StringComparison.OrdinalIgnoreCase));

        _providers = registered
            .Where(x => !string.Equals(
                x.ProviderCode,
                HermesGenericAgentProvider.FallbackProviderCode,
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.ProviderCode, StringComparer.OrdinalIgnoreCase);
    }

    public IAgentProvider Resolve(string providerCode)
    {
        if (_providers.TryGetValue(providerCode, out var provider))
        {
            return provider;
        }

        return _fallback
            ?? throw new InvalidOperationException(
                $"No agent provider is registered for '{providerCode}'.");
    }
}
