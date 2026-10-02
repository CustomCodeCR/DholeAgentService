using Dhole.Agent.Infrastructure.Providers.Maersk.Resolvers;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskEquipmentResolverTests
{
    private readonly MaerskEquipmentResolver _sut = new();

    [DataTestMethod]
    [DataRow("22G1", MaerskEquipmentResolver.TwentyDryStandard)]
    [DataRow("20DV", MaerskEquipmentResolver.TwentyDryStandard)]
    [DataRow("20STD", MaerskEquipmentResolver.TwentyDryStandard)]
    [DataRow("42G1", MaerskEquipmentResolver.FortyDryStandard)]
    [DataRow("40DV", MaerskEquipmentResolver.FortyDryStandard)]
    [DataRow("40STD", MaerskEquipmentResolver.FortyDryStandard)]
    [DataRow("45G1", MaerskEquipmentResolver.FortyDryHigh)]
    [DataRow("40HC", MaerskEquipmentResolver.FortyDryHigh)]
    [DataRow("45HC", MaerskEquipmentResolver.FortyFiveDryHigh)]
    [DataRow("L5G1", MaerskEquipmentResolver.FortyFiveDryHigh)]
    [DataRow("22T3", MaerskEquipmentResolver.TwentyTank)]
    [DataRow("42T3", MaerskEquipmentResolver.FortyTank)]
    [DataRow("22R1", MaerskEquipmentResolver.TwentyReeferStandard)]
    [DataRow("45R1", MaerskEquipmentResolver.FortyReeferHigh)]
    [DataRow("42R1", MaerskEquipmentResolver.FortyReeferStandard)]
    [DataRow("22U1", MaerskEquipmentResolver.TwentyOpenTop)]
    [DataRow("42U1", MaerskEquipmentResolver.FortyOpenTop)]
    [DataRow("45U1", MaerskEquipmentResolver.FortyOpenTopHigh)]
    [DataRow("42P3", MaerskEquipmentResolver.FortyFlatStandard)]
    [DataRow("45P3", MaerskEquipmentResolver.FortyFlatHigh)]
    [DataRow("22P1", MaerskEquipmentResolver.TwentyFlat)]
    [DataRow("20 TANK", MaerskEquipmentResolver.TwentyTank)]
    [DataRow("40 TANK", MaerskEquipmentResolver.FortyTank)]
    [DataRow("20NOR", MaerskEquipmentResolver.TwentyReeferStandard)]
    [DataRow("20 NOR", MaerskEquipmentResolver.TwentyReeferStandard)]
    [DataRow("40NOR", MaerskEquipmentResolver.FortyReeferHigh)]
    [DataRow("40 REEFER STANDARD", MaerskEquipmentResolver.FortyReeferStandard)]
    [DataRow("20OT", MaerskEquipmentResolver.TwentyOpenTop)]
    [DataRow("40OT", MaerskEquipmentResolver.FortyOpenTop)]
    [DataRow("40 OPEN TOP HIGH", MaerskEquipmentResolver.FortyOpenTopHigh)]
    [DataRow("40 FLAT STANDARD", MaerskEquipmentResolver.FortyFlatStandard)]
    [DataRow("40 FLAT HIGH", MaerskEquipmentResolver.FortyFlatHigh)]
    [DataRow("20 FLAT", MaerskEquipmentResolver.TwentyFlat)]
    public void Normalize_ShouldReturnExactMaerskEquipmentLabel(
        string input,
        string expected)
    {
        Assert.AreEqual(expected, _sut.Normalize(input));
    }
}
