using System.Diagnostics;
using Dhole.Agent.Application.Abstractions.Runtime;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.Infrastructure.Browser;

/// <summary>
/// Process- and host-independent exclusion for workers sharing the persistent
/// Chromium volume. File locks are tied to the OS file handle, not an expiring
/// Redis TTL: a crashed worker releases the claim and an active browser cannot
/// lose ownership simply because a 10-minute login ran past a lease timeout.
/// </summary>
public sealed class FileSystemBrowserProfileExclusiveLock(
    IOptions<BrowserOptions> options) : IBrowserProfileExclusiveLock
{
    private readonly string _root = Path.GetFullPath(options.Value.ProfilesPath);
    private readonly TimeSpan _wait = TimeSpan.FromSeconds(
        Math.Clamp(options.Value.ProfileLockWaitSeconds, 1, 30));

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string providerCode, Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerCode);

        var safeProvider = string.Concat(providerCode.ToUpperInvariant()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        if (safeProvider.Length == 0)
            throw new ArgumentException("Invalid provider key.", nameof(providerCode));

        // A shared volume must have the same lock regardless of the environment
        // variable. Separate staging/prod volumes remain isolated by _root.
        var folder = Path.Combine(_root, ".locks", safeProvider);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, credentialId.ToString("N") + ".lock");
        var timer = Stopwatch.StartNew();

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var stream = new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new HeldLock(stream);
            }
            catch (IOException) when (timer.Elapsed < _wait)
            {
                await Task.Delay(200, cancellationToken);
            }
            catch (IOException)
            {
                // A second profile instance owns the handle, or its volume is
                // unavailable. Fail closed: do not open Chromium without a lock.
                return null;
            }
        } while (timer.Elapsed < _wait);

        return null;
    }

    private sealed class HeldLock(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
