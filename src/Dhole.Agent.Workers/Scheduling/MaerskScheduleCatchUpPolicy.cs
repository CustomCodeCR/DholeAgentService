namespace Dhole.Agent.Workers.Scheduling;

/// <summary>
/// Maersk scheduled searches never catch up old due times after an
/// authentication/circuit pause or Worker downtime. Other providers retain
/// their existing scheduling behavior.
/// </summary>
public static class MaerskScheduleCatchUpPolicy
{
    // Allow small scheduler/host timing jitter without replaying historical jobs.
    public static readonly TimeSpan DueGrace = TimeSpan.FromMinutes(1);

    public static bool IsOverdue(DateTime dueAtUtc, DateTime nowUtc)
        => dueAtUtc < nowUtc.Subtract(DueGrace);

    public static bool ShouldSkip(DateTime dueAtUtc, DateTime nowUtc, bool providerBlocked)
        => providerBlocked || IsOverdue(dueAtUtc, nowUtc);
}
