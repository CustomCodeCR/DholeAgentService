namespace Dhole.Agent.Infrastructure.Providers.Maersk.Resolvers;

public sealed class MaerskEquipmentResolver
{
    public const string TwentyDryStandard = "20 DRY STANDARD (20DV)";
    public const string FortyDryStandard = "40 DRY STANDARD (40DV)";
    public const string FortyDryHigh = "40 DRY HIGH (40HC)";
    public const string FortyFiveDryHigh = "45 DRY HIGH (45HC)";
    public const string TwentyTank = "20 TANK";
    public const string FortyTank = "40 TANK";
    public const string TwentyReeferStandard = "20 REEFER STANDARD (20 NOR)";
    public const string FortyReeferHigh = "40 REEFER HIGH (40NOR)";
    public const string FortyReeferStandard = "40 REEFER STANDARD";
    public const string TwentyOpenTop = "20 OPEN TOP (20OT)";
    public const string FortyOpenTop = "40 OPEN TOP(40OT)";
    public const string FortyOpenTopHigh = "40 OPEN TOP HIGH";
    public const string FortyFlatStandard = "40 FLAT STANDARD";
    public const string FortyFlatHigh = "40 FLAT HIGH";
    public const string TwentyFlat = "20 FLAT";

    public string Normalize(string value)
    {
        var normalized = NormalizeKey(value);

        return normalized switch
        {
            "20DV" or "20STD" or "20DRY" or "20DRYSTANDARD"
                => TwentyDryStandard,

            "40DV" or "40STD" or "40DRY" or "40DRYSTANDARD"
                => FortyDryStandard,

            "40HC" or "40DRYHIGH" or "40HIGHCUBE" or "40HIGHCUBEDRY"
                => FortyDryHigh,

            "45HC" or "45DRYHIGH" or "45HIGHCUBE" or "45HIGHCUBEDRY"
                => FortyFiveDryHigh,

            "20TANK"
                => TwentyTank,

            "40TANK"
                => FortyTank,

            "20NOR" or "20REEFERSTANDARD"
                => TwentyReeferStandard,

            "40NOR" or "40REEFERHIGH"
                => FortyReeferHigh,

            "40REEFERSTANDARD"
                => FortyReeferStandard,

            "20OT" or "20OPENTOP"
                => TwentyOpenTop,

            "40OT" or "40OPENTOP"
                => FortyOpenTop,

            "40OPENTOPHIGH"
                => FortyOpenTopHigh,

            "40FLATSTANDARD"
                => FortyFlatStandard,

            "40FLATHIGH"
                => FortyFlatHigh,

            "20FLAT"
                => TwentyFlat,

            _ => value.Trim()
        };
    }

    private static string NormalizeKey(string value)
        => new(value
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
}
