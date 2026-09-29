using Dhole.Agent.Infrastructure.Providers.Maersk.Resolvers;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskEquipmentResolverTests
{
    private readonly MaerskEquipmentResolver _sut = new();

    [DataTestMethod]
    [DataRow("20DV", MaerskEquipmentResolver.TwentyDryStandard)]
    [DataRow("20STD", MaerskEquipmentResolver.TwentyDryStandard)]
    [DataRow("40DV", MaerskEquipmentResolver.FortyDryStandard)]
    [DataRow("40STD", MaerskEquipmentResolver.FortyDryStandard)]
    [DataRow("40HC", MaerskEquipmentResolver.FortyDryHigh)]
    [DataRow("45HC", MaerskEquipmentResolver.FortyFiveDryHigh)]
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
