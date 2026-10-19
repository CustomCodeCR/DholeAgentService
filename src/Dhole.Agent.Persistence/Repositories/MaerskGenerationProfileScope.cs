using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.Persistence.Repositories;

/// <summary>
/// PostgreSQL identifies the active production generation for API operations;
/// the Worker uses Browser__Generation to select only its own persisted profile.
/// An obsolete generation's profile must never satisfy active session checks.
/// </summary>
public static class MaerskGenerationProfileScope
{
    public static async Task<string> GetActiveAsync(ServiceDbContext db, CancellationToken ct)
    {
        var active = await db.Database.SqlQueryRaw<string>(
            "SELECT active_generation AS \"Value\" FROM agent.runtime_generation_state WHERE singleton=true")
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(active) || !IsGeneration(active))
            throw new InvalidOperationException("Active Maersk runtime generation is not initialized.");
        return active;
    }

    public static string Prefix(string generation)
    {
        if (!IsGeneration(generation))
            throw new ArgumentException("Invalid active generation.", nameof(generation));
        return "maersk-" + generation.ToLowerInvariant() + "-";
    }

    private static bool IsGeneration(string value)
    {
        if (value == "prod") return true;
        if (!value.StartsWith("prod", StringComparison.Ordinal) || value.Length <= 4)
            return false;
        var digits = value.AsSpan(4);
        return digits[0] >= '1' && digits[0] <= '9' &&
            digits.ToString().All(c => c >= '0' && c <= '9');
    }
}
