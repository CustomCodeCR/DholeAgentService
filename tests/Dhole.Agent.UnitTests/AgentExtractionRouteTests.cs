using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class AgentExtractionRouteTests
{
    [TestMethod]
    public void Create_AllowsMissingPodAndNormalizesPorts()
    {
        var route = AgentExtractionRoute.Create(Guid.NewGuid(), null, "CNSHA", " Shanghai ",
            "CRCAL", " Caldera ", null, null);
        Assert.AreEqual("Shanghai", route.PolName);
        Assert.AreEqual("Caldera", route.PoeName);
        Assert.IsNull(route.PodName);
        Assert.IsNull(route.PodCode);
    }

    [TestMethod]
    public void Update_CanClearExistingPod()
    {
        var route = AgentExtractionRoute.Create(Guid.NewGuid(), null, "CNSHA", "Shanghai",
            "CRCAL", "Caldera", "CRSJO", "San José");
        route.Update(null, "CNSHA", "Shanghai", "CRCAL", "Caldera", " ", " ", true, 0);
        Assert.IsNull(route.PodName);
        Assert.IsNull(route.PodCode);
    }

    [TestMethod]
    public void Create_RequiresPolAndPoe()
    {
        Assert.ThrowsExactly<ArgumentException>(() => AgentExtractionRoute.Create(
            Guid.NewGuid(), null, null, " ", null, "Caldera", null, null));
        Assert.ThrowsExactly<ArgumentException>(() => AgentExtractionRoute.Create(
            Guid.NewGuid(), null, null, "Shanghai", null, " ", null, "San José"));
    }

    [TestMethod]
    public void Update_RequiresPoe()
    {
        var route = AgentExtractionRoute.Create(Guid.NewGuid(), null, null, "Shanghai",
            null, "Caldera", null, null);
        Assert.ThrowsExactly<ArgumentException>(() => route.Update(
            null, null, "Shanghai", null, null, null, null, true, 0));
    }
}
