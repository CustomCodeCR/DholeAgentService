namespace Dhole.Agent.Infrastructure.Runtime.Hermes;

public sealed class HermesOptions
{
    public const string SectionName = "Hermes";

    public string BaseUrl { get; set; } = "http://hermes-agent:8642";
    public string ExecutePath { get; set; } = "/v1/responses";
    public string ApiKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = "hermes-agent";
    public int TimeoutSeconds { get; set; } = 600;
}
