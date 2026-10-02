namespace Dhole.Agent.Infrastructure.Providers.Maersk.Resolvers;

public sealed class MaerskLocationResolver
{
    public string Normalize(string value)
        => Normalize(value, null);

    public string Normalize(string value, string? locationCode)
    {
        // UN/LOCODE is metadata only. Never require code-specific behavior for a
        // route: new POL/POE/POD values must work without a deployment.
        _ = locationCode;

        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var cleaned = CollapseWhitespace(value);
        var parts = cleaned
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var locality = parts.Length > 0 ? parts[0] : cleaned;
        var tail = parts.Length > 1
            ? string.Join(", ", parts.Skip(1))
            : string.Empty;

        // Normalize common catalog/user naming variants to the human locality
        // Maersk's typeahead expects. This is generic, not port-specific.
        var parenthesis = locality.IndexOf('(');
        if (parenthesis > 0)
            locality = locality[..parenthesis].Trim();

        var tokens = locality
            .Split(
                [' ', '-', '/', '_'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !GenericLocationWords.Contains(token))
            .ToArray();

        if (tokens.Length > 0)
            locality = string.Join(" ", tokens);

        return tail.Length > 0
            ? $"{locality}, {tail}"
            : locality;
    }

    private static string CollapseWhitespace(string value)
        => string.Join(
            " ",
            value.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static readonly HashSet<string> GenericLocationWords =
        new(
            [
                "PORT",
                "PUERTO",
                "HARBOR",
                "HARBOUR",
                "TERMINAL",
                "CITY",
                "OF",
                "DE",
                "DEL",
                "THE"
            ],
            StringComparer.OrdinalIgnoreCase);
}
