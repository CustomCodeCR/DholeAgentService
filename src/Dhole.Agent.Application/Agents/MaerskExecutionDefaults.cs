using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dhole.Agent.Application.Agents;

public static class MaerskExecutionDefaults
{
    private static readonly TimeSpan CostaRicaOffset = TimeSpan.FromHours(-6);

    public static DateOnly GetCargoReadyDate()
    {
        var costaRicaNow = DateTimeOffset.UtcNow.ToOffset(CostaRicaOffset);
        return DateOnly.FromDateTime(costaRicaNow.Date).AddDays(1);
    }

    public static string NormalizeInputJson(string? inputJson)
    {
        JsonObject root;

        try
        {
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson) as JsonObject
                ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        root["cargoReadyDate"] = GetCargoReadyDate().ToString("yyyy-MM-dd");

        return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
