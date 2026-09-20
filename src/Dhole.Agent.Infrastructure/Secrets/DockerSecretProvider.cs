using Dhole.Agent.Application.Abstractions.Runtime;

namespace Dhole.Agent.Infrastructure.Secrets;

public sealed class DockerSecretProvider(string rootPath = "/run/secrets") : ISecretProvider
{
    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var safeName = Path.GetFileName(key);
        var path = Path.Combine(rootPath, safeName);
        if (!File.Exists(path))
            return null;
        return (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
    }
}
