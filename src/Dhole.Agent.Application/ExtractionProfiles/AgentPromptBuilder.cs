using System.Text.Json;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.ExtractionProfiles;

public sealed class AgentPromptBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string Build(
        AgentExtractionProfile profile,
        AgentProvider provider,
        IReadOnlyCollection<AgentExtractionRoute> routes,
        IReadOnlyCollection<AgentExtractionEquipment> equipment,
        IReadOnlyCollection<AgentExtractionField> fields,
        IReadOnlyCollection<AgentEndpointCapture> captureRules,
        DateOnly? cargoReadyDate = null,
        Guid? executionId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(provider);

        var prompt = profile.PromptTemplate;
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{providerName}}"] = provider.Name,
            ["{{providerCode}}"] = provider.Code,
            ["{{baseUrl}}"] = profile.BaseUrl ?? string.Empty,
            ["{{loginUrl}}"] = profile.LoginUrl ?? string.Empty,
            ["{{searchUrl}}"] = profile.SearchUrl ?? string.Empty,
            ["{{routes}}"] = SerializeRoutes(routes),
            ["{{equipment}}"] = SerializeEquipment(equipment),
            ["{{fields}}"] = SerializeFields(fields),
            ["{{captureRules}}"] = SerializeCaptureRules(captureRules),
            ["{{cargoReadyDate}}"] = cargoReadyDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            ["{{executionId}}"] = executionId?.ToString() ?? string.Empty
        };

        foreach (var pair in replacements)
            prompt = prompt.Replace(pair.Key, pair.Value, StringComparison.Ordinal);

        return prompt.Trim();
    }

    public IReadOnlyCollection<string> AvailableVariables { get; } =
    [
        "{{providerName}}",
        "{{providerCode}}",
        "{{baseUrl}}",
        "{{loginUrl}}",
        "{{searchUrl}}",
        "{{routes}}",
        "{{equipment}}",
        "{{fields}}",
        "{{captureRules}}",
        "{{cargoReadyDate}}",
        "{{executionId}}"
    ];

    private static string SerializeRoutes(IEnumerable<AgentExtractionRoute> routes)
        => JsonSerializer.Serialize(routes.Where(x => x.IsActive && !x.IsDeleted).OrderBy(x => x.SortOrder).Select(x => new
        {
            x.Id,
            x.Name,
            x.PolCode,
            x.PolName,
            x.PoeCode,
            x.PoeName,
            x.PodCode,
            x.PodName
        }), JsonOptions);

    private static string SerializeEquipment(IEnumerable<AgentExtractionEquipment> equipment)
        => JsonSerializer.Serialize(equipment.Where(x => x.IsActive && !x.IsDeleted).OrderBy(x => x.SortOrder).Select(x => new
        {
            x.Id,
            x.Code,
            x.Name,
            x.Quantity,
            x.DefaultWeightKg
        }), JsonOptions);

    private static string SerializeFields(IEnumerable<AgentExtractionField> fields)
        => JsonSerializer.Serialize(fields.Where(x => x.IsActive && !x.IsDeleted).OrderBy(x => x.SortOrder).Select(x => new
        {
            x.Key,
            x.Label,
            x.Description,
            dataType = x.DataType.ToString(),
            sourceType = x.SourceType.ToString(),
            x.JsonPath,
            x.Required
        }), JsonOptions);

    private static string SerializeCaptureRules(IEnumerable<AgentEndpointCapture> captures)
        => JsonSerializer.Serialize(captures.Where(x => x.IsActive && !x.IsDeleted).OrderBy(x => x.SortOrder).Select(x => new
        {
            x.Name,
            x.HttpMethod,
            x.UrlPattern,
            matchType = x.MatchType.ToString(),
            x.ContentType,
            x.CaptureRequest,
            x.CaptureResponse,
            x.IsRequired,
            x.TimeoutSeconds
        }), JsonOptions);
}
