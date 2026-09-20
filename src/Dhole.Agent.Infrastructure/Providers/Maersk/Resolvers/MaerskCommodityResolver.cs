namespace Dhole.Agent.Infrastructure.Providers.Maersk.Resolvers;

public sealed class MaerskCommodityResolver
{
    public string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? "General Cargo" : value.Trim();
}
