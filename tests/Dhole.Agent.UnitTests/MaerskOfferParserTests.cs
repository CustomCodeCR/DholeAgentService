using Dhole.Agent.Infrastructure.Providers.Maersk.Parsers;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskOfferParserTests
{
    private static string LoadFixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Maersk",
            "departures-offers-response.json");

        return File.ReadAllText(path);
    }

    [TestMethod]
    public void Parse_ShouldIgnoreNotOffered()
    {
        var result = new MaerskOfferParser().Parse(LoadFixture());

        Assert.IsFalse(result.Offers.Any(x => x.ExternalRouteId == "ROUTE-NOT-OFFERED"));
    }

    [TestMethod]
    public void Parse_ShouldIgnoreUnavailable()
    {
        var result = new MaerskOfferParser().Parse(LoadFixture());

        Assert.IsFalse(result.Offers.Any(x => x.ExternalRouteId == "ROUTE-UNAVAILABLE"));
    }

    [TestMethod]
    public void Parse_ShouldExtractBasicOceanFreight()
    {
        var offer = new MaerskOfferParser().Parse(LoadFixture()).Offers.Single();

        Assert.IsNotNull(offer.OceanFreight);
        Assert.AreEqual("USD", offer.OceanFreight.Currency);
        Assert.AreEqual(1900m, offer.OceanFreight.Amount);
    }

    [TestMethod]
    public void Parse_ShouldExtractAllIn()
    {
        var offer = new MaerskOfferParser().Parse(LoadFixture()).Offers.Single();

        Assert.IsNotNull(offer.AllIn);
        Assert.AreEqual("USD", offer.AllIn.Currency);
        Assert.AreEqual(2500m, offer.AllIn.Amount);
    }

    [TestMethod]
    public void Parse_ShouldExtractRouteSchedule()
    {
        var offer = new MaerskOfferParser().Parse(LoadFixture()).Offers.Single();

        Assert.AreEqual(new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero), offer.Etd);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero), offer.Eta);
        Assert.AreEqual(27, offer.TransitDays);
        Assert.AreEqual(1, offer.Legs.Count);
    }

    [TestMethod]
    public void Parse_ShouldExtractVesselAndVoyage()
    {
        var offer = new MaerskOfferParser().Parse(LoadFixture()).Offers.Single();

        Assert.AreEqual("MAERSK TEST", offer.Vessel);
        Assert.AreEqual("123W", offer.Voyage);
    }

    [TestMethod]
    public void Parse_ShouldGroupProductsByRouteId()
    {
        var result = new MaerskOfferParser().Parse(LoadFixture());

        Assert.AreEqual(1, result.Offers.Count);
        var offer = result.Offers.Single();
        CollectionAssert.AreEquivalent(
            new[] { "MaerskSpot", "MaerskSpotWithVR" },
            offer.Products.ToArray());
    }

    [TestMethod]
    public void Parse_ShouldNotDuplicateSpotAndSpotWithVR()
    {
        var result = new MaerskOfferParser().Parse(LoadFixture());

        Assert.AreEqual(1, result.Offers.Count);
        Assert.AreEqual("ROUTE-001", result.Offers.Single().ExternalRouteId);
    }
}
