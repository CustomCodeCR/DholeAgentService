using Dhole.Agent.Application.Abstractions.Runtime;

namespace Dhole.Agent.Infrastructure.Runtime.Hermes;

public sealed class HermesAgentRuntime(HermesClient client) : IAgentRuntime
{
    public Task<string> ExecuteAsync(
        string instruction,
        string? contextJson,
        CancellationToken cancellationToken = default)
        => client.ExecuteAsync(instruction, contextJson, cancellationToken);
}
