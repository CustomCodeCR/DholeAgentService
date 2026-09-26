using System.Text.Json;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.ExtractionProfiles;

public sealed record AgentExecutionSnapshot(string Prompt, string ConfigurationJson);

public sealed class AgentExecutionSnapshotBuilder(AgentPromptBuilder promptBuilder)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AgentExecutionSnapshot Build(
        AgentExtractionProfile profile,
        AgentProvider provider,
        IReadOnlyCollection<AgentExtractionRoute> routes,
        IReadOnlyCollection<AgentExtractionEquipment> equipment,
        IReadOnlyCollection<AgentExtractionField> fields,
        IReadOnlyCollection<AgentEndpointCapture> captureRules,
        DateOnly? cargoReadyDate,
        Guid executionId)
    {
        var prompt = promptBuilder.Build(profile, provider, routes, equipment, fields, captureRules, cargoReadyDate, executionId);

        var configuration = JsonSerializer.Serialize(new
        {
            profileId = profile.Id,
            profile.ProviderId,
            profile.Name,
            profile.BaseUrl,
            profile.LoginUrl,
            profile.SearchUrl,
            profile.PromptTemplate,
            executionStrategy = profile.ExecutionStrategy.ToString(),
            profile.ParserKey,
            routes = routes.Where(x => x.IsActive && !x.IsDeleted).OrderBy(x => x.SortOrder).Select(x => new
            {
                x.Id, x.Name, x.PolCode, x.PolName, x.PoeCode, x.PoeName, x.PodCode, x.PodName
            }),
            equipment = equipment.Where(x => x.IsActive && !x.IsDeleted).OrderBy(x => x.SortOrder).Select(x => new
            {
                x.Id, x.Code, x.Name, x.Quantity, x.DefaultWeightKg
            }),
            captureRules = captureRules.Where(x => x.IsActive && !x.IsDeleted).OrderBy(x => x.SortOrder).Select(x => new
            {
                x.Id, x.Name, x.HttpMethod, x.UrlPattern, matchType = x.MatchType.ToString(), x.ContentType,
                x.CaptureRequest, x.CaptureResponse, x.IsRequired, x.TimeoutSeconds
            }),
            fields = fields.Where(x => x.IsActive && !x.IsDeleted).OrderBy(x => x.SortOrder).Select(x => new
            {
                x.Id, x.Key, x.Label, x.Description, dataType = x.DataType.ToString(), sourceType = x.SourceType.ToString(),
                x.JsonPath, x.Required
            }),
            cargoReadyDate
        }, JsonOptions);

        return new AgentExecutionSnapshot(prompt, configuration);
    }
}
