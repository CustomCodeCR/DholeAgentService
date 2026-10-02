namespace Dhole.Agent.Infrastructure.Providers.Maersk.Resolvers;

public sealed class MaerskLocationResolver
{
    public string Normalize(string value)
        => Normalize(value, null);

    public string Normalize(string value, string? locationCode)
    {
        var trimmed = value.Trim();
        var code = (locationCode ?? string.Empty)
            .Trim()
            .ToUpperInvariant();

        return code switch
        {
            // Maersk's booking UI identifies CRCAL as "Puerto Caldera".
            // Using only "Caldera" can resolve to Caldera, Chile.
            "CRCAL" => "Puerto Caldera, Costa Rica",
            _ => trimmed
        };
    }
}
