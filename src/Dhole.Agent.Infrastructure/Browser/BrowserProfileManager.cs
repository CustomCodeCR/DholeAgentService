using Dhole.Agent.Application.Abstractions.Runtime;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.Infrastructure.Browser;

public sealed class BrowserProfileManager(IOptions<BrowserOptions> options) : IBrowserProfileManager
{
    private readonly string _profilesRoot = Path.GetFullPath(options.Value.ProfilesPath);

    public string GetStoragePath(string providerCode, Guid credentialId)
    {
        var path = BuildStoragePath(providerCode, credentialId);
        Directory.CreateDirectory(path);
        return path;
    }

    public string ResetStoragePath(string providerCode, Guid credentialId)
    {
        var path = BuildStoragePath(providerCode, credentialId);

        if (Directory.Exists(path))
        {
            var stalePath = $"{path}.stale-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            Directory.Move(path, stalePath);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private string BuildStoragePath(string providerCode, Guid credentialId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerCode);

        var safeProvider = string.Concat(
            providerCode
                .ToUpperInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

        if (string.IsNullOrWhiteSpace(safeProvider))
            throw new ArgumentException(
                "Provider code does not contain a valid path segment.",
                nameof(providerCode));

        return Path.Combine(
            _profilesRoot,
            safeProvider,
            credentialId.ToString("N"));
    }
}
