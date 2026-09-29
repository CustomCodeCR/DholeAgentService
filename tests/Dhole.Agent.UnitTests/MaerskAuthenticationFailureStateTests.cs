using System.Reflection;
using Dhole.Agent.Application.Runtime;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskAuthenticationFailureStateTests
{
    [DataTestMethod]
    [DataRow("maersk_authentication_verification_required")]
    [DataRow("maersk_authentication_unauthorized")]
    [DataRow("maersk_authentication_forbidden")]
    [DataRow("maersk_authentication_rate_limited")]
    [DataRow("maersk_authentication_continue_not_clickable")]
    [DataRow("maersk_authentication_callback_timeout")]
    [DataRow("maersk_post_auth_navigation_failed")]
    public void AuthenticationFailures_ShouldWaitForAuthentication(string code)
    {
        var method = typeof(AgentExecutionOrchestrator).GetMethod(
            "IsAuthenticationRequiredFailure",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);
        Assert.IsTrue((bool)method!.Invoke(null, [code])!);
    }
}
