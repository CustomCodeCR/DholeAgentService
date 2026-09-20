using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class AgentProviderTests
{
    [TestMethod]
    public void Create_ShouldNormalizeCodeAndRaiseEvent()
    {
        var provider = AgentProvider.Create(" maersk ", "Maersk", AgentProviderType.Maersk,
            "https://www.maersk.com", AgentExecutionStrategy.BrowserNetworkCapture, true, null);

        Assert.AreEqual("MAERSK", provider.Code);
        Assert.IsTrue(provider.IsActive);
        Assert.AreEqual(1, provider.DomainEvents.Count);
    }

    [TestMethod]
    public void SetActive_ShouldChangeState()
    {
        var provider = AgentProvider.Create("MSC", "MSC", AgentProviderType.Msc, null,
            AgentExecutionStrategy.Browser, false, null);

        provider.SetActive(false);

        Assert.IsFalse(provider.IsActive);
    }
}
