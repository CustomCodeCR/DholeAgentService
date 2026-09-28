using System.Text.Json;
using Dhole.Agent.Application.Agents;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskExecutionDefaultsTests
{
    [TestMethod]
    public void NormalizeInputJson_ShouldAlwaysUseCostaRicaTomorrow()
    {
        var normalized = MaerskExecutionDefaults.NormalizeInputJson(
            """{"commodity":"New - TIRES, TYRES, RUBBER","cargoReadyDate":"2026-09-25"}""");

        using var document = JsonDocument.Parse(normalized);
        var root = document.RootElement;

        Assert.AreEqual(
            MaerskExecutionDefaults.GetCargoReadyDate().ToString("yyyy-MM-dd"),
            root.GetProperty("cargoReadyDate").GetString());
        Assert.AreEqual(
            "New - TIRES, TYRES, RUBBER",
            root.GetProperty("commodity").GetString());
    }
}
