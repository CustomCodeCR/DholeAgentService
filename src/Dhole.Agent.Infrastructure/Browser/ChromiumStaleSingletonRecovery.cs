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

    public static bool TryArchiveOrphanedLinks(string profilePath)
    {
        try
        {
            var directory = new DirectoryInfo(profilePath);
            if (!directory.Exists || directory.LinkTarget is not null)
                return false;

            var links = Markers.Select(name => new FileInfo(
                Path.Combine(directory.FullName, name))).ToArray();

            // All three must be dangling symbolic links. Never touch regular
            // files or any link whose target is present (including a live socket).
            if (links.Any(link => link.LinkTarget is null ||
                File.Exists(link.FullName) || Directory.Exists(link.FullName)))
                return false;

            var lockTarget = links[0].LinkTarget!;
            var pidMatch = LockPidPattern().Match(lockTarget);
            if (!pidMatch.Success || !int.TryParse(pidMatch.Groups[1].Value, out var pid)
                || pid <= 0)
                return false;

            // PID reuse is common after a Worker restart. An unrelated live PID
            // does not own Chromium's lock. A real browser process does.
            if (AnyChromiumProcessRunning() || ChromiumOwnsPid(pid))
                return false;

            var archive = Path.Combine(directory.FullName,
                ".dhole-orphaned-singletons-" +
                DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(archive);
            foreach (var link in links)
                File.Move(link.FullName, Path.Combine(archive, link.Name));

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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

    private static bool AnyChromiumProcessRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (IsChromiumProcessName(process.ProcessName))
                            return true;
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited while enumerating.
                    }
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static bool IsChromiumProcessName(string name) =>
        name.StartsWith("chrome", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("chromium", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("headless_shell", StringComparison.OrdinalIgnoreCase);
}
