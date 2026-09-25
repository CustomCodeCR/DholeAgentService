using System.Text.Json;
using Dhole.Agent.Application.Abstractions.Runtime;

namespace Dhole.Agent.Infrastructure.Providers.Generic;

public sealed class HermesGenericAgentProvider(IAgentRuntime runtime) : IAgentProvider
{
    public const string FallbackProviderCode = "*";

    public string ProviderCode => FallbackProviderCode;

    public async Task<AgentProviderExecutionResult> ExecuteAsync(
        AgentExecutionContext context,
        CancellationToken cancellationToken)
    {
        var providerName = string.IsNullOrWhiteSpace(context.Provider.Name)
            ? context.Provider.Code
            : context.Provider.Name;

        var instruction = string.IsNullOrWhiteSpace(context.Execution.PromptSnapshot)
            ? BuildFallbackInstruction(context, providerName)
            : context.Execution.PromptSnapshot!;

        if (!string.IsNullOrWhiteSpace(context.Execution.ConfigurationSnapshotJson))
        {
            instruction += string.Concat(
                Environment.NewLine,
                Environment.NewLine,
                "Execution profile configuration snapshot:",
                Environment.NewLine,
                context.Execution.ConfigurationSnapshotJson);
        }

        if (!string.IsNullOrWhiteSpace(context.Definition.ConfigurationJson))
        {
            instruction += string.Concat(
                Environment.NewLine,
                Environment.NewLine,
                "Definition configuration:",
                Environment.NewLine,
                context.Definition.ConfigurationJson);
        }

        try
        {
            var rawOutput = await runtime.ExecuteAsync(
                instruction,
                context.Execution.InputJson,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(rawOutput))
            {
                return AgentProviderExecutionResult.Failed(
                    "hermes_empty_response",
                    "Hermes returned an empty response.");
            }

            var dataJson = NormalizeJson(rawOutput);
            var outputJson = JsonSerializer.Serialize(
                new
                {
                    provider = context.Provider.Code,
                    providerName,
                    extractionProfileId = context.Execution.ExtractionProfileId,
                    strategy = "Hermes",
                    action = context.Definition.ActionType.ToString()
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            return AgentProviderExecutionResult.Completed(
                outputJson,
                "CarrierExtraction",
                "1.0",
                dataJson);
        }
        catch (Exception ex)
        {
            return AgentProviderExecutionResult.Failed("hermes_provider_error", ex.Message);
        }
    }

    private static string BuildFallbackInstruction(AgentExecutionContext context, string providerName)
    {
        var baseUrl = string.IsNullOrWhiteSpace(context.Provider.BaseUrl)
            ? "not configured"
            : context.Provider.BaseUrl;

        return $"""
            You are executing a carrier data extraction for {providerName} ({context.Provider.Code}).
            Provider base URL: {baseUrl}
            Action: {context.Definition.ActionType}
            Definition: {context.Definition.Name}

            Use the available browser tools when web interaction is required.
            Follow the provider website safely and do not submit purchases, bookings, or irreversible actions.
            Extract the requested freight/routing information from the input context.
            Return only valid JSON. Preserve prices, currencies, ETD, ETA, transit time, vessel,
            voyage, route legs and charge breakdown whenever they are available.
            """;
    }

    private static string NormalizeJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(
                new { content = value },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
    }
}
