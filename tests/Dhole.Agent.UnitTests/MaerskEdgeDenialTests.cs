using System.Reflection;
using Dhole.Agent.Application.Runtime;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskEdgeDenialTests
{
    [TestMethod]
    public void EdgeDenied_ShouldWaitForAuthentication()
    {
        var method = typeof(AgentExecutionOrchestrator).GetMethod(
            "IsAuthenticationRequiredFailure",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);
        Assert.IsTrue((bool)method!.Invoke(
            null,
            ["maersk_authentication_edge_denied"])!);
    }
}
