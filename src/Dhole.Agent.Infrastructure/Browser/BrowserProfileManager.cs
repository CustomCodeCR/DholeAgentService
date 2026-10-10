using Dhole.Agent.Application.Abstractions.Runtime;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.Infrastructure.Browser;

public sealed class BrowserProfileManager(IOptions<BrowserOptions> options) : IBrowserProfileManager
{
    private readonly string _profilesRoot = Path.GetFullPath(options.Value.ProfilesPath);
    private readonly int _maxRetainedBackups = Math.Clamp(
        options.Value.MaxRetainedProfileBackups, 1, 500);

    public string GetStoragePath(string providerCode, Guid credentialId)
    {
        var path = BuildStoragePath(providerCode, credentialId);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Explicitly requested technical repair only. The caller must own the
    /// exclusive profile lock and must have closed Chromium before this call.
    /// Never use to work around a challenge, HTTP 403 or HTTP 429.
    /// </summary>
    public string ResetStoragePath(string providerCode, Guid credentialId)
    {
        var path = BuildStoragePath(providerCode, credentialId);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);

        string? archived = null;
        if (Directory.Exists(path))
        {
            if (new DirectoryInfo(path).LinkTarget is not null)
                throw new IOException("Cannot repair a symbolic-link browser profile.");

            var backupPrefix = Path.GetFileName(path) + ".stale-";
            var existingBackups = Directory
                .EnumerateDirectories(parent, backupPrefix + "*", SearchOption.TopDirectoryOnly)
                .Take(_maxRetainedBackups)
                .Count();
            if (existingBackups >= _maxRetainedBackups)
                throw new IOException(
                    "Browser profile backup retention limit reached. Review archives manually before requesting another repair.");

            archived = $"{path}.stale-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            Directory.Move(path, archived);

            if (!Directory.Exists(archived))
                throw new IOException("Browser profile archive was not verified.");
        }

        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch
        {
            // Failure while creating the replacement must not silently lose
            // the original Chromium state. Do not overwrite any new files.
            if (archived is not null && !Directory.Exists(path))
                Directory.Move(archived, path);

            throw;
        }
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
