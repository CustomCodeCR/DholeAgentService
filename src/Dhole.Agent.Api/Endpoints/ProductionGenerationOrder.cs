using System.Globalization;
using System.Numerics;

namespace Dhole.Agent.Api.Endpoints;

/// <summary>Strict, irreversible generation succession: prod, prod1, prod2 ...</summary>
internal static class ProductionGenerationOrder
{
    internal static bool IsForward(string? destination, string? active)
    {
        static BigInteger? Ordinal(string? name)
        {
            if (name == "prod") return BigInteger.Zero;
            if (name is null || name.Length is < 5 or > 64 ||
                !name.StartsWith("prod", StringComparison.Ordinal) ||
                name[4] is < '1' or > '9' ||
                !name.Skip(5).All(static c => c is >= '0' and <= '9'))
                return null;
            return BigInteger.TryParse(name.AsSpan(4), NumberStyles.None,
                CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        var next = Ordinal(destination);
        var current = Ordinal(active);
        return next.HasValue && current.HasValue && next.Value > current.Value;
    }
}
