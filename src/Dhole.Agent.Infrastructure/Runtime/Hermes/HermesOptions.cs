namespace Dhole.Agent.Infrastructure.Runtime.Hermes;

public sealed class HermesOptions
{
    public const string SectionName = "Hermes";

    public string BaseUrl { get; set; } = "http://hermes-agent:8642";
    public string ExecutePath { get; set; } = "/v1/chat/completions";
    public string Model { get; set; } = "hermes-agent";
    public string ApiKey { get; set; } = string.Empty;

    // Total execution fallback when a schedule does not provide one.
    public int TimeoutSeconds { get; set; } = 600;

    // Each Route x Equipment search is isolated so one slow browser/tool call
    // cannot consume the entire execution window.
    public int TaskTimeoutSeconds { get; set; } = 300;

    // Keep this conservative because each Hermes task can open browser/tool work.
    public int MaxParallelTasks { get; set; } = 2;
}
