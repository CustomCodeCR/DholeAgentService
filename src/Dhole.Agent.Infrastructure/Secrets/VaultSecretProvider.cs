using Dhole.Agent.Application.Abstractions.Runtime;

namespace Dhole.Agent.Infrastructure.Secrets;

// Placeholder adapter boundary for a future external Vault implementation.
// It is deliberately not registered until a concrete Vault client is configured.
public sealed class VaultSecretProvider : ISecretProvider
{
    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("VaultSecretProvider requires a configured Vault client.");
}
