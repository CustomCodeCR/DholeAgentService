namespace Dhole.Agent.Infrastructure.Providers.Maersk.Resolvers;

public sealed class MaerskEquipmentResolver
{
    public string Normalize(string value) => value.Trim().ToUpperInvariant() switch
    {
        "40HC" or "40HC" or "40 HIGH CUBE" or "40 HIGH CUBE DRY" => "40HC",
        "40STD" or "40DRY" or "40 DRY" => "40STD",
        "20STD" or "20DRY" or "20 DRY" => "20STD",
        var other => other
    };
}
