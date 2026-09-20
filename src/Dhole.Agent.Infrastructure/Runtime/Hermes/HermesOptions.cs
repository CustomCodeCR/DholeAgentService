namespace Dhole.Agent.Infrastructure.Runtime.Hermes;

public sealed class HermesOptions
{
    public const string SectionName = "Hermes";
    public string BaseUrl { get; set; } = "http://hermes-agent:8000";
    public string ExecutePath { get; set; } = "/v1/agent/run";
    public int TimeoutSeconds { get; set; } = 120;
}
