using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Dhole.Agent.Infrastructure.Browser;

/// <summary>
/// Technical recovery for Chromium's transient singleton symlinks.
/// Only called while holding the exclusive profile lock after a launch failure.
/// Never edits authentication, cookies, browser preferences or directories.
/// </summary>
public static partial class ChromiumStaleSingletonRecovery
{
    private static readonly string[] Markers =
        ["SingletonLock", "SingletonSocket", "SingletonCookie"];

    [GeneratedRegex(@"-([0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LockPidPattern();

    public static bool TryArchiveOrphanedLinks(string profilePath, Func<bool>? chromiumRunning = null)
    {
        if (chromiumRunning?.Invoke() == true)
            return false;

        return TryArchiveOrphanedLinks(profilePath, out _);
    }

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

            var links = Markers.Select(name =>
                new FileInfo(Path.Combine(directory.FullName, name))).ToArray();

            // The lock is positive evidence of Chromium's owner. Never infer
            // orphaned state from just a socket/cookie or from an ordinary file.
            if (links[0].LinkTarget is null)
            {
                diagnostic = "singleton_lock_missing_or_regular";
                return false;
            }

            foreach (var link in links)
            {
                if (link.LinkTarget is null)
                {
                    // Chromium may leave only one or two symlinks after a crash.
                    // Missing entries are safe; a regular file is NOT.
                    if (Path.Exists(link.FullName))
                    {
                        diagnostic = "singleton_marker_not_symlink";
                        return false;
                    }
                }
                else if (LinkTargetExists(link))
                {
                    diagnostic = "singleton_link_target_exists";
                    return false;
                }
            }

            var pidMatch = LockPidPattern().Match(links[0].LinkTarget!);
            if (!pidMatch.Success ||
                !int.TryParse(pidMatch.Groups[1].Value, out var pid) || pid <= 0)
            {
                diagnostic = "invalid_lock_process_id";
                return false;
            }

            if (ChromiumOwnsPid(pid))
            {
                diagnostic = "browser_process_still_running";
                return false;
            }

            var archive = Path.Combine(directory.FullName,
                ".dhole-orphaned-singletons-" +
                DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff",
                    System.Globalization.CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(archive);
            foreach (var link in links.Where(item => item.LinkTarget is not null))
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
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return true; }
    }

    private static bool IsChromiumProcessName(string name) =>
        name.StartsWith("chrome", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("chromium", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("headless_shell", StringComparison.OrdinalIgnoreCase);
}
