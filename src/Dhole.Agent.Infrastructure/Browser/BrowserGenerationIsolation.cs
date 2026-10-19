using System.Text.RegularExpressions;

namespace Dhole.Agent.Infrastructure.Browser;

/// <summary>
/// Binds a persistent browser volume to exactly one production generation.
/// The marker is created atomically and never changes or moves Chromium state.
/// A mis-mounted volume fails closed instead of sharing another generation's cookies.
/// </summary>
public static class BrowserGenerationIsolation
{
    public static void EnsureBound(string profilesRoot, string? generation)
    {
        if (string.IsNullOrWhiteSpace(generation))
            return; // Legacy/local workers without a generation setting.

        generation = generation.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(generation, @"^[a-z][a-z0-9-]{0,63}$",
                RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid browser generation identifier.", nameof(generation));

        var root = Path.GetFullPath(profilesRoot);
        var rootInfo = new DirectoryInfo(root);
        if (rootInfo.Exists && rootInfo.LinkTarget is not null)
            throw new IOException("Browser profile root cannot be a symbolic link.");

        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, ".dhole-generation");
        var temporary = Path.Combine(root, ".dhole-generation-pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Move is atomic on this volume. Do not open/truncate an existing marker.
            File.WriteAllText(temporary, generation + Environment.NewLine);
            try
            {
                File.Move(temporary, marker);
            }
            catch (IOException) when (File.Exists(marker))
            {
                // Another process already bound the volume.
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        var markerInfo = new FileInfo(marker);
        if (markerInfo.LinkTarget is not null)
            throw new IOException("Browser generation marker cannot be a symbolic link.");

        var boundGeneration = File.ReadAllText(marker).Trim();
        if (!string.Equals(boundGeneration, generation, StringComparison.Ordinal))
            throw new IOException(
                $"Browser profile volume belongs to generation '{boundGeneration}', not '{generation}'. " +
                "Refusing to share persistent browser sessions.");
    }
}
