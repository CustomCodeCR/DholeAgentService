using System.Text.Json;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.ExtractionProfiles;

public sealed class AgentExecutionPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyCollection<AgentExecutionTask> Plan(
        Guid executionId,
        IReadOnlyCollection<AgentExtractionRoute> routes,
        IReadOnlyCollection<AgentExtractionEquipment> equipment,
        DateOnly cargoReadyDate,
        string commodity = "FAK")
    {
        var activeRoutes = routes.Where(x => x.IsActive && !x.IsDeleted).OrderBy(x => x.SortOrder).ToArray();
        var activeEquipment = equipment.Where(x => x.IsActive && !x.IsDeleted).OrderBy(x => x.SortOrder).ToArray();

        var tasks = new List<AgentExecutionTask>(activeRoutes.Length * activeEquipment.Length);
        var sortOrder = 0;

        foreach (var route in activeRoutes)
        {
            foreach (var item in activeEquipment)
            {
                var inputJson = JsonSerializer.Serialize(new
                {
                    routeId = route.Id,
                    equipmentId = item.Id,
                    pol = route.PolName,
                    poe = route.PoeName,
                    pod = route.PodName,
                    containerType = item.Code,
                    quantity = item.Quantity,
                    weightKg = item.DefaultWeightKg,
                    commodity = string.IsNullOrWhiteSpace(commodity) ? "FAK" : commodity.Trim(),
                    cargoReadyDate
                }, JsonOptions);

                tasks.Add(AgentExecutionTask.Create(executionId, route.Id, item.Id, inputJson, sortOrder++));
            }
        }

        return tasks;
    }
}
