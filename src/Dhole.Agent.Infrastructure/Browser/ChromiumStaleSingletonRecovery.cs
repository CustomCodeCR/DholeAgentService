using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Dhole.Agent.Infrastructure.Browser;

/// <summary>
/// Technical recovery for Chromium's three transient singleton symlinks.
/// Call ONLY while the provider's exclusive profile lock is held, after a
/// Playwright launch failure. Sessions, cookies, preferences and directories
/// are never deleted or replaced.
/// </summary>
public static partial class ChromiumStaleSingletonRecovery
{
    private static readonly string[] Markers =
        ["SingletonLock", "SingletonSocket", "SingletonCookie"];

    [GeneratedRegex(@"-([0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LockPidPattern();

    public static bool TryArchiveOrphanedLinks(string profilePath) =>
        TryArchiveOrphanedLinks(profilePath, out _);

    public static bool TryArchiveOrphanedLinks(string profilePath, out string diagnostic)
    {
        diagnostic = "not_eligible";
        try
        {
            var directory = new DirectoryInfo(profilePath);
            if (!directory.Exists || directory.LinkTarget is not null)
            {
                diagnostic = "profile_directory_not_safe";
                return false;
            }

            var links = Markers.Select(name => new FileInfo(
                Path.Combine(directory.FullName, name))).ToArray();

            // All three must be dangling symbolic links. Never touch regular
            // files or any link whose target is present (including a live socket).
            if (links.Any(link => link.LinkTarget is null ||
                LinkTargetExists(link)))
            {
                diagnostic = "singleton_links_not_orphaned";
                return false;
            }

            var lockTarget = links[0].LinkTarget!;
            var pidMatch = LockPidPattern().Match(lockTarget);
            if (!pidMatch.Success || !int.TryParse(pidMatch.Groups[1].Value, out var pid)
                || pid <= 0)
            {
                diagnostic = "invalid_lock_process_id";
                return false;
            }

            // PID reuse is common after a Worker restart. An unrelated live PID
            // does not own Chromium's lock. A real browser process does.
            if (ChromiumOwnsPid(pid))
            {
                diagnostic = "browser_process_still_running";
                return false;
            }

            var archive = Path.Combine(directory.FullName,
                ".dhole-orphaned-singletons-" +
                DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(archive);
            foreach (var link in links)
                File.Move(link.FullName, Path.Combine(archive, link.Name));

            diagnostic = "orphaned_links_archived";
            return true;
        }
        catch (IOException)
        {
            diagnostic = "filesystem_io_error";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            diagnostic = "filesystem_permission_error";
            return false;
        }
    }

    private static bool LinkTargetExists(FileInfo link)
    {
        // .NET File.Exists/Path.Exists on a broken symlink can report the
        // directory entry itself. Resolve the link target explicitly.
        var target = link.LinkTarget!;
        var resolved = Path.IsPathFullyQualified(target)
            ? target
            : Path.GetFullPath(Path.Combine(link.DirectoryName!, target));
        return Path.Exists(resolved);
    }

    private static bool ChromiumOwnsPid(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return IsChromiumProcessName(process.ProcessName);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // Unable to establish ownership: do not rewrite the profile.
            return true;
        }
    }

    private static bool IsChromiumProcessName(string name) =>
        name.StartsWith("chrome", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("chromium", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("headless_shell", StringComparison.OrdinalIgnoreCase);
}
