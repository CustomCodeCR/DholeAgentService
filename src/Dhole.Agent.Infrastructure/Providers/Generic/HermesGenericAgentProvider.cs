using System.Text;
using System.Text.Json;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Infrastructure.Runtime.Hermes;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.Infrastructure.Providers.Generic;

public sealed class HermesGenericAgentProvider(
    IAgentRuntime runtime,
    IOptions<HermesOptions> options) : IAgentProvider
{
    public const string FallbackProviderCode = "*";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HermesOptions _options = options.Value;

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

        var routes = ReadArray(profileConfiguration.Value, "routes");
        var equipment = ReadArray(profileConfiguration.Value, "equipment");
        var fields = ReadArray(profileConfiguration.Value, "fields");
        var captureRules = ReadArray(profileConfiguration.Value, "captureRules");

        if (routes.Count == 0 || equipment.Count == 0)
        {
            return AgentProviderExecutionResult.Failed(
                "extraction_profile_plan_empty",
                "The selected extraction profile must contain at least one active route and one active equipment.");
        }

        var runtimeOverrides = ReadRuntimeOverrides(context.Execution.InputJson);
        var searches = BuildSearchPlan(routes, equipment);
        var totalTimeoutSeconds = Math.Max(30, context.TimeoutSeconds ?? _options.TimeoutSeconds);
        var taskTimeoutSeconds = Math.Clamp(
            _options.TaskTimeoutSeconds,
            30,
            totalTimeoutSeconds);
        var maxParallelTasks = Math.Clamp(
            _options.MaxParallelTasks,
            1,
            Math.Min(4, searches.Count));

        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        executionCts.CancelAfter(TimeSpan.FromSeconds(totalTimeoutSeconds));
        using var gate = new SemaphoreSlim(maxParallelTasks, maxParallelTasks);

        var taskCalls = searches.Select(search => ExecuteSearchAsync(
            context,
            providerName,
            profileConfiguration.Value,
            fields,
            captureRules,
            runtimeOverrides,
            search,
            gate,
            taskTimeoutSeconds,
            executionCts.Token));

        var outcomes = await Task.WhenAll(taskCalls);

        var completedCount = outcomes.Count(x => !x.IsError);
        var availableCount = outcomes.Count(x => string.Equals(x.Status, "Available", StringComparison.OrdinalIgnoreCase));
        var failedCount = outcomes.Count(x => x.IsError);

        if (completedCount == 0)
        {
            var timeoutOnly = outcomes.All(x => string.Equals(x.ErrorCode, "hermes_task_timeout", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(x.ErrorCode, "hermes_execution_timeout", StringComparison.OrdinalIgnoreCase));

            return AgentProviderExecutionResult.Failed(
                timeoutOnly ? "hermes_timeout" : "hermes_all_tasks_failed",
                BuildFailureSummary(outcomes, searches.Count, totalTimeoutSeconds, taskTimeoutSeconds));
        }

        var errors = outcomes
            .Where(x => x.IsError)
            .Select(x => new
            {
                x.TaskIndex,
                x.RouteId,
                x.EquipmentId,
                x.ErrorCode,
                x.ErrorMessage
            })
            .ToArray();

        var data = new
        {
            results = outcomes.Select(x => x.Result).ToArray(),
            errors
        };

        var outputJson = JsonSerializer.Serialize(
            new
            {
                provider = context.Provider.Code,
                providerName,
                extractionProfileId = context.Execution.ExtractionProfileId,
                strategy = "Hermes",
                action = context.Definition.ActionType.ToString(),
                plannedSearchCount = searches.Count,
                completedSearchCount = completedCount,
                availableSearchCount = availableCount,
                failedSearchCount = failedCount,
                maxParallelTasks,
                taskTimeoutSeconds,
                totalTimeoutSeconds,
                data
            },
            JsonOptions);

        var resultType = context.Definition.ActionType == AgentActionType.SearchOceanRates
            ? AgentResult.OceanFreightRates
            : "CarrierExtraction";

        return failedCount > 0
            ? AgentProviderExecutionResult.Partial(outputJson, resultType, "3.0", outputJson)
            : AgentProviderExecutionResult.Completed(outputJson, resultType, "3.0", outputJson);
    }

    private async Task<SearchOutcome> ExecuteSearchAsync(
        AgentExecutionContext context,
        string providerName,
        JsonElement profileConfiguration,
        IReadOnlyList<JsonElement> fields,
        IReadOnlyList<JsonElement> captureRules,
        IReadOnlyDictionary<string, object?> runtimeOverrides,
        PlannedSearch search,
        SemaphoreSlim gate,
        int taskTimeoutSeconds,
        CancellationToken executionToken)
    {
        var acquired = false;

        try
        {
            await gate.WaitAsync(executionToken);
            acquired = true;

            var instruction = BuildTaskInstruction(
                context,
                providerName,
                profileConfiguration,
                search,
                fields,
                captureRules,
                runtimeOverrides);

            var taskContext = BuildTaskContextJson(
                profileConfiguration,
                search,
                fields,
                captureRules,
                runtimeOverrides);

            var rawOutput = await runtime.ExecuteAsync(
                instruction,
                taskContext,
                executionToken,
                taskTimeoutSeconds);

            return NormalizeTaskResult(search, fields, rawOutput);
        }
        catch (TimeoutException ex)
        {
            return CreateErrorOutcome(
                search,
                "hermes_task_timeout",
                ex.Message);
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            return CreateErrorOutcome(
                search,
                "hermes_execution_timeout",
                "The overall Hermes execution deadline was reached before this search could complete.");
        }
        catch (Exception ex)
        {
            return CreateErrorOutcome(
                search,
                "hermes_task_error",
                ex.Message);
        }
        finally
        {
            if (acquired)
                gate.Release();
        }
    }

    private static string BuildTaskInstruction(
        AgentExecutionContext context,
        string providerName,
        JsonElement profileConfiguration,
        PlannedSearch search,
        IReadOnlyList<JsonElement> fields,
        IReadOnlyList<JsonElement> captureRules,
        IReadOnlyDictionary<string, object?> runtimeOverrides)
    {
        var template = TryGetString(profileConfiguration, "promptTemplate");
        var cargoReadyDate = runtimeOverrides.TryGetValue("cargoReadyDate", out var cargoDate)
            ? Convert.ToString(cargoDate)
            : TryGetString(profileConfiguration, "cargoReadyDate");

        var routeJson = JsonSerializer.Serialize(new[] { search.Route }, JsonOptions);
        var equipmentJson = JsonSerializer.Serialize(new[] { search.Equipment }, JsonOptions);
        var fieldsJson = JsonSerializer.Serialize(fields, JsonOptions);
        var capturesJson = JsonSerializer.Serialize(captureRules, JsonOptions);

        string profilePrompt;
        if (!string.IsNullOrWhiteSpace(template))
        {
            profilePrompt = template
                .Replace("{{providerName}}", providerName, StringComparison.Ordinal)
                .Replace("{{providerCode}}", context.Provider.Code, StringComparison.Ordinal)
                .Replace("{{baseUrl}}", TryGetString(profileConfiguration, "baseUrl") ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{loginUrl}}", TryGetString(profileConfiguration, "loginUrl") ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{searchUrl}}", TryGetString(profileConfiguration, "searchUrl") ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{routes}}", routeJson, StringComparison.Ordinal)
                .Replace("{{equipment}}", equipmentJson, StringComparison.Ordinal)
                .Replace("{{fields}}", fieldsJson, StringComparison.Ordinal)
                .Replace("{{captureRules}}", capturesJson, StringComparison.Ordinal)
                .Replace("{{cargoReadyDate}}", cargoReadyDate ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{executionId}}", context.Execution.Id.ToString(), StringComparison.Ordinal);
        }
        else
        {
            profilePrompt = $"Extract carrier data from {providerName} ({context.Provider.Code}) using the configured profile.";
        }

        var fieldKeys = ReadFieldKeys(fields);
        var builder = new StringBuilder(profilePrompt.Trim());

        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("TASK SCOPE — EXECUTE ONLY THIS ONE SEARCH:");
        builder.AppendLine($"Task index: {search.Index}");
        builder.AppendLine($"Route: {search.Route.GetRawText()}");
        builder.AppendLine($"Equipment: {search.Equipment.GetRawText()}");
        builder.AppendLine($"Required field keys: {string.Join(", ", fieldKeys)}");
        builder.AppendLine();
        builder.AppendLine("RULES:");
        builder.AppendLine("- The route and equipment above are authoritative. Do not substitute values from any schedule input.");
        builder.AppendLine("- Use the profile URLs and capture rules. Do not invent prices, dates, vessels, routes or availability.");
        builder.AppendLine("- Use runtime overrides only for cargoReadyDate, commodity and instruction when present.");
        builder.AppendLine("- Complete this single search and return immediately; do not iterate other profile routes or equipment.");
        builder.AppendLine("- If no offer exists, return status Unavailable with null configured fields.");
        builder.AppendLine("- If the search cannot be completed, return status Error and explain why.");
        builder.AppendLine("- Return ONLY valid JSON in exactly this shape:");
        builder.AppendLine(
            """
            {
              "status": "Available|Unavailable|Error",
              "fields": {
                "configuredFieldKey": "value|null"
              },
              "error": "string|null"
            }
            """);

        return builder.ToString();
    }

    private static string BuildTaskContextJson(
        JsonElement profileConfiguration,
        PlannedSearch search,
        IReadOnlyList<JsonElement> fields,
        IReadOnlyList<JsonElement> captureRules,
        IReadOnlyDictionary<string, object?> runtimeOverrides)
    {
        return JsonSerializer.Serialize(
            new
            {
                profile = new
                {
                    profileId = TryGetString(profileConfiguration, "profileId"),
                    name = TryGetString(profileConfiguration, "name"),
                    baseUrl = TryGetString(profileConfiguration, "baseUrl"),
                    loginUrl = TryGetString(profileConfiguration, "loginUrl"),
                    searchUrl = TryGetString(profileConfiguration, "searchUrl"),
                    parserKey = TryGetString(profileConfiguration, "parserKey"),
                    route = search.Route,
                    equipment = search.Equipment,
                    fields,
                    captureRules
                },
                runtimeOverrides
            },
            JsonOptions);
    }

    private static SearchOutcome NormalizeTaskResult(
        PlannedSearch search,
        IReadOnlyList<JsonElement> configuredFields,
        string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return CreateErrorOutcome(
                search,
                "hermes_empty_response",
                "Hermes returned an empty response for this search.");
        }

        try
        {
            using var document = JsonDocument.Parse(rawOutput);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return CreateErrorOutcome(
                    search,
                    "hermes_invalid_task_response",
                    "Hermes task response must be a JSON object.");
            }

            var payload = root.TryGetProperty("result", out var nested)
                          && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;

            var status = TryGetString(payload, "status");
            var error = TryGetString(payload, "error");
            var fields = ExtractConfiguredFields(payload, configuredFields);

            if (string.IsNullOrWhiteSpace(status))
                status = string.IsNullOrWhiteSpace(error) ? "Available" : "Error";

            if (!status.Equals("Available", StringComparison.OrdinalIgnoreCase)
                && !status.Equals("Unavailable", StringComparison.OrdinalIgnoreCase)
                && !status.Equals("Error", StringComparison.OrdinalIgnoreCase))
            {
                status = "Error";
                error ??= "Hermes returned an unsupported task status.";
            }

            var result = CreateResultElement(search, status, fields, error);
            var isError = status.Equals("Error", StringComparison.OrdinalIgnoreCase);

            return new SearchOutcome(
                search.Index,
                GetId(search.Route),
                GetId(search.Equipment),
                status,
                result,
                isError,
                isError ? "hermes_task_failed" : null,
                isError ? error : null);
        }
        catch (JsonException ex)
        {
            return CreateErrorOutcome(
                search,
                "hermes_invalid_task_response",
                $"Hermes returned invalid JSON for this search: {ex.Message}");
        }
    }

    private static JsonElement ExtractConfiguredFields(
        JsonElement payload,
        IReadOnlyList<JsonElement> configuredFields)
    {
        if (payload.TryGetProperty("fields", out var directFields)
            && directFields.ValueKind == JsonValueKind.Object)
        {
            return directFields.Clone();
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in configuredFields)
        {
            var key = TryGetString(field, "key");
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (TryGetPropertyIgnoreCase(payload, key, out var value))
                values[key] = value.Clone();
            else
                values[key] = JsonSerializer.SerializeToElement<object?>(null, JsonOptions);
        }

        return JsonSerializer.SerializeToElement(values, JsonOptions);
    }

    private static JsonElement CreateResultElement(
        PlannedSearch search,
        string status,
        JsonElement fields,
        string? error)
    {
        var route = search.Route;
        var equipment = search.Equipment;

        return JsonSerializer.SerializeToElement(
            new
            {
                taskIndex = search.Index,
                routeId = GetId(route),
                equipmentId = GetId(equipment),
                route,
                equipment = new
                {
                    id = GetId(equipment),
                    code = TryGetString(equipment, "code"),
                    name = TryGetString(equipment, "name"),
                    quantity = TryGetInt(equipment, "quantity"),
                    weightKg = TryGetDecimal(equipment, "defaultWeightKg")
                },
                status,
                fields,
                error
            },
            JsonOptions);
    }

    private static SearchOutcome CreateErrorOutcome(
        PlannedSearch search,
        string errorCode,
        string errorMessage)
    {
        var emptyFields = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>(),
            JsonOptions);

        var result = CreateResultElement(
            search,
            "Error",
            emptyFields,
            errorMessage);

        return new SearchOutcome(
            search.Index,
            GetId(search.Route),
            GetId(search.Equipment),
            "Error",
            result,
            true,
            errorCode,
            errorMessage);
    }

    private static IReadOnlyList<PlannedSearch> BuildSearchPlan(
        IReadOnlyList<JsonElement> routes,
        IReadOnlyList<JsonElement> equipment)
    {
        var result = new List<PlannedSearch>(routes.Count * equipment.Count);
        var index = 1;

        foreach (var route in routes)
        {
            foreach (var item in equipment)
            {
                result.Add(new PlannedSearch(
                    index++,
                    route.Clone(),
                    item.Clone()));
            }
        }

        return result;
    }

    private static IReadOnlyList<JsonElement> ReadArray(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private static IReadOnlyList<string> ReadFieldKeys(
        IReadOnlyList<JsonElement> fields)
    {
        return fields
            .Select(x => TryGetString(x, "key"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();
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
                if (!TryGetPropertyIgnoreCase(document.RootElement, key, out var value))
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
            // Legacy schedule input is optional because the profile owns the search plan.
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
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildFailureSummary(
        IReadOnlyCollection<SearchOutcome> outcomes,
        int plannedCount,
        int totalTimeoutSeconds,
        int taskTimeoutSeconds)
    {
        var details = outcomes
            .Take(5)
            .Select(x => $"task {x.TaskIndex}: {x.ErrorCode} - {x.ErrorMessage}");

        return $"Hermes completed 0 of {plannedCount} planned searches. "
             + $"Total timeout={totalTimeoutSeconds}s, task timeout={taskTimeoutSeconds}s. "
             + string.Join(" | ", details);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ValueKind == JsonValueKind.Null
                ? null
                : value.ToString();
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        return int.TryParse(value.ToString(), out number) ? number : null;
    }

    private static decimal? TryGetDecimal(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        return decimal.TryParse(value.ToString(), out number) ? number : null;
    }

    private static string? GetId(JsonElement element)
        => TryGetString(element, "id");

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out value))
                return true;

            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed record PlannedSearch(
        int Index,
        JsonElement Route,
        JsonElement Equipment);

    private sealed record SearchOutcome(
        int TaskIndex,
        string? RouteId,
        string? EquipmentId,
        string Status,
        JsonElement Result,
        bool IsError,
        string? ErrorCode,
        string? ErrorMessage);
}
