namespace Dhole.Agent.Application.Runtime;

public sealed class AgentQueueOptions
{
    public const string SectionName = "AgentQueue";
    // Disabled until migration, staging validation, and a coordinated worker rollout.
    public bool ConcurrentDispatcherEnabled { get; set; } = false;
    public int MaxConcurrentExecutions { get; set; } = 4;
    public int MaxConcurrentMaersk { get; set; } = 1;
    public int MaxConcurrentPerBrowserProfile { get; set; } = 1;
    public int PollIntervalSeconds { get; set; } = 1;
    public int LeaseSeconds { get; set; } = 180;
    public int HeartbeatSeconds { get; set; } = 20;
    public int MaxTransientRetries { get; set; } = 2;
    public int[] RetryBackoffSeconds { get; set; } = [15, 60];

    public void Validate()
    {
        if (MaxConcurrentExecutions is < 1 or > 32
            || MaxConcurrentMaersk != 1
            || MaxConcurrentPerBrowserProfile != 1
            || PollIntervalSeconds is < 1 or > 30
            || LeaseSeconds is < 60 or > 3600
            || HeartbeatSeconds is < 5
            || HeartbeatSeconds * 3 >= LeaseSeconds
            || MaxTransientRetries is < 0 or > 5
            || RetryBackoffSeconds is null || RetryBackoffSeconds.Length < Math.Max(1, MaxTransientRetries)
            || RetryBackoffSeconds.Any(x => x is < 1 or > 3600))
            throw new InvalidOperationException("AgentQueue settings are invalid or unsafe.");
    }

    public int RetryDelayForAttempt(int attempt)
        => RetryBackoffSeconds[Math.Clamp(attempt - 1, 0, RetryBackoffSeconds.Length - 1)];
}
