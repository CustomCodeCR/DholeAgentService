using System.Text.Json;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Infrastructure.Providers.Generic;

public sealed class HermesGenericAgentProvider(IAgentRuntime runtime) : IAgentProvider
{
    public const string FallbackProviderCode = "*";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ProviderCode => FallbackProviderCode;

    public async Task<AgentProviderExecutionResult> ExecuteAsync(
        AgentExecutionContext context,
        CancellationToken cancellationToken)
    {
        var providerName = string.IsNullOrWhiteSpace(context.Provider.Name)
            ? context.Provider.Code
            : context.Provider.Name;

        var profileConfiguration = ParseObject(context.Execution.ConfigurationSnapshotJson);
        if (profileConfiguration is null)
        {
            return AgentProviderExecutionResult.Failed(
                "extraction_profile_snapshot_missing",
                "The execution does not contain a valid extraction profile configuration snapshot.");
        }

        var plannedSearchCount = GetArrayLength(profileConfiguration.Value, "routes")
            * GetArrayLength(profileConfiguration.Value, "equipment");

        if (plannedSearchCount <= 0)
        {
            return AgentProviderExecutionResult.Failed(
                "extraction_profile_plan_empty",
                "The selected extraction profile must contain at least one active route and one active equipment.");
        }

        var instruction = BuildAuthoritativeInstruction(
            context,
            providerName,
            profileConfiguration.Value,
            plannedSearchCount);

        var executionContextJson = BuildExecutionContextJson(
            profileConfiguration.Value,
            context.Execution.InputJson);

        try
        {
            var rawOutput = await runtime.ExecuteAsync(
                instruction,
                executionContextJson,
                cancellationToken,
                context.TimeoutSeconds);

            if (string.IsNullOrWhiteSpace(rawOutput))
            {
                return AgentProviderExecutionResult.Failed(
                    "hermes_empty_response",
                    "Hermes returned an empty response.");
            }

            var normalized = NormalizeExtractionJson(rawOutput);
            if (!normalized.Success)
            {
                return AgentProviderExecutionResult.Failed(
                    "hermes_invalid_extraction",
                    normalized.Error ?? "Hermes did not return the required extraction result structure.");
            }

            var resultCount = normalized.ResultCount;
            var outputJson = JsonSerializer.Serialize(
                new
                {
                    provider = context.Provider.Code,
                    providerName,
                    extractionProfileId = context.Execution.ExtractionProfileId,
                    strategy = "Hermes",
                    action = context.Definition.ActionType.ToString(),
                    plannedSearchCount,
                    completedSearchCount = resultCount,
                    data = normalized.Data
                },
                JsonOptions);

            var resultType = context.Definition.ActionType == AgentActionType.SearchOceanRates
                ? AgentResult.OceanFreightRates
                : "CarrierExtraction";

            if (resultCount < plannedSearchCount)
            {
                return AgentProviderExecutionResult.Partial(
                    outputJson,
                    resultType,
                    "2.0",
                    outputJson);
            }

            return AgentProviderExecutionResult.Completed(
                outputJson,
                resultType,
                "2.0",
                outputJson);
        }
        catch (TimeoutException ex)
        {
            return AgentProviderExecutionResult.Failed("hermes_timeout", ex.Message);
        }
        catch (Exception ex)
        {
            return AgentProviderExecutionResult.Failed("hermes_provider_error", ex.Message);
        }
    }

    private static string BuildAuthoritativeInstruction(
        AgentExecutionContext context,
        string providerName,
        JsonElement profileConfiguration,
        int plannedSearchCount)
    {
        var profilePrompt = string.IsNullOrWhiteSpace(context.Execution.PromptSnapshot)
            ? BuildFallbackInstruction(context, providerName)
            : context.Execution.PromptSnapshot!;

        var fieldKeys = ReadFieldKeys(profileConfiguration);
        var fieldList = fieldKeys.Count == 0
            ? "all configured fields"
            : string.Join(", ", fieldKeys);

        return $$"""
{{profilePrompt}}

MANDATORY EXECUTION RULES:
1. The extraction profile configuration is the SOURCE OF TRUTH for routes, equipment, URLs, capture rules and requested fields.
2. Do NOT replace profile routes or profile equipment with POL, POD, containerType, quantity or weight values from runtime/schedule input.
3. Execute EVERY active Route × Equipment combination from the profile. Planned searches: {{plannedSearchCount}}.
4. For each equipment use its profile quantity and defaultWeightKg.
5. Extract the configured fields for every search. Requested field keys: {{fieldList}}.
6. Runtime overrides are supplementary only. cargoReadyDate, commodity and instruction may be used when present; they never change profile routes/equipment.
7. Use the configured baseUrl/loginUrl/searchUrl and configured capture rules. Do not invent URLs, routes, equipment, prices or schedules.
8. If a route/equipment combination has no available offer, include that combination with status "Unavailable" and null fields. If a search fails, include it with status "Error" and an error message.
9. Do not report the job as complete by returning a summary only. Return one result entry for every planned Route × Equipment search.
10. Return ONLY valid JSON with this exact top-level structure:
{
  "results": [
    {
      "routeId": "guid",
      "equipmentId": "guid",
      "route": {
        "polCode": "string|null",
        "polName": "string",
        "poeCode": "string|null",
        "poeName": "string|null",
        "podCode": "string|null",
        "podName": "string|null"
      },
      "equipment": {
        "code": "string",
        "name": "string",
        "quantity": 1,
        "weightKg": 15000
      },
      "status": "Available|Unavailable|Error",
      "fields": {
        "configuredFieldKey": "value|null"
      },
      "error": "string|null"
    }
  ],
  "errors": []
}

The "fields" object must use the configured extraction field keys exactly.
"""
    }

    private static string BuildExecutionContextJson(
        JsonElement profileConfiguration,
        string? scheduleInputJson)
    {
        var runtimeOverrides = ReadRuntimeOverrides(scheduleInputJson);

        return JsonSerializer.Serialize(
            new
            {
                profile = profileConfiguration,
                runtimeOverrides
            },
            JsonOptions);
    }

    private static Dictionary<string, object?> ReadRuntimeOverrides(string? inputJson)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(inputJson))
            return result;

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var key in new[] { "cargoReadyDate", "commodity", "instruction" })
            {
                if (!document.RootElement.TryGetProperty(key, out var value))
                    continue;

                result[key] = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => value.Clone()
                };
            }
        }
        catch (JsonException)
        {
            // Invalid legacy schedule input is ignored because the profile is authoritative.
        }

        return result;
    }

    private static JsonElement? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int GetArrayLength(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;
    }

    private static IReadOnlyList<string> ReadFieldKeys(JsonElement profileConfiguration)
    {
        if (!profileConfiguration.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array)
            return [];

        var keys = new List<string>();
        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object
                || !field.TryGetProperty("key", out var key)
                || key.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(key.GetString()))
                continue;

            keys.Add(key.GetString()!);
        }

        return keys;
    }

    private static (bool Success, JsonElement? Data, int ResultCount, string? Error) NormalizeExtractionJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return (false, null, 0, "Hermes must return a JSON object.");

            if (!root.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
                return (false, null, 0, "Hermes response is missing the required 'results' array.");

            if (results.GetArrayLength() == 0)
                return (false, null, 0, "Hermes returned zero extraction results.");

            return (true, root.Clone(), results.GetArrayLength(), null);
        }
        catch (JsonException ex)
        {
            return (false, null, 0, $"Hermes returned invalid JSON: {ex.Message}");
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
Return only verified structured data from the provider.
""";
    }
}
