namespace Dhole.Agent.Infrastructure.Browser;

public sealed class BrowserOptions
{
    public const string SectionName = "Browser";
    public string ProfilesPath { get; set; } = "/data/browser-profiles";
    public bool Headless { get; set; } = false;
    public int DefaultTimeoutMs { get; set; } = 60_000;
}
