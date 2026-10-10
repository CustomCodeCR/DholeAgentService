namespace Dhole.Agent.Infrastructure.Browser;

public sealed class BrowserOptions
{
    public const string SectionName = "Browser";
    public string ProfilesPath { get; set; } = "/data/browser-profiles";
    public bool Headless { get; set; } = false;
    public int DefaultTimeoutMs { get; set; } = 60_000;
    public int ProfileLockWaitSeconds { get; set; } = 5;
    // Fail safely at the limit; never delete authentication backups automatically.
    public int MaxRetainedProfileBackups { get; set; } = 20;
}
