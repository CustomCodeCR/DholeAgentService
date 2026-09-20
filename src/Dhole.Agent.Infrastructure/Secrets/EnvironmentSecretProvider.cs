using Dhole.Agent.Application.Abstractions.Runtime;

namespace Dhole.Agent.Infrastructure.Secrets;

public sealed class EnvironmentSecretProvider : ISecretProvider
{
    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Task.FromResult(Environment.GetEnvironmentVariable(key));
    }
}
