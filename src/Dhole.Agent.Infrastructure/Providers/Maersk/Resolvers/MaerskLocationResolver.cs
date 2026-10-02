namespace Dhole.Agent.Infrastructure.Providers.Maersk.Resolvers;

public sealed class MaerskLocationResolver
{
    public string Normalize(string value)
        => Normalize(value, null);

    public string Normalize(string value, string? locationCode)
    {
        // Location behavior must not be hardcoded by UN/LOCODE. Routes are
        // configuration data and new POL/POE/POD entries must work without a
        // code deployment. Browser automation resolves the human label against
        // Maersk's live typeahead and validates the returned suggestion.
        _ = locationCode;

        return string.Join(
            " ",
            value.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
