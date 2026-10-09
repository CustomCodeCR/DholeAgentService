using System.Reflection;
using Dhole.Agent.Infrastructure.Providers.Maersk.Authentication;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskAuthenticationPolicyTests
{
    [TestMethod]
    public void GenericGlobalAccountsError_ShouldNotBeTreatedAsCredentialRejection()
    {
        var method = typeof(MaerskLoginService).GetMethod(
            "LooksLikeAuthenticationFailure",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);

        var result = (bool)method!.Invoke(
            null,
            ["Something went wrong\n[object Object]"])!;

        Assert.IsFalse(result);
    }

    [DataTestMethod]
    [DataRow("https://accounts.maersk.com/ocean-maeu/auth/login?nonce=expired&code_challenge=expired")]
    [DataRow("https://accounts.maersk.com/ocean-maeu/auth/login?state=old")]
    public void ExpiredOidcLoginUrl_ShouldUseStableEntryPoint(string configuredLoginUrl)
    {
        var method = typeof(MaerskLoginService).GetMethod(
            "ResolveLoginUrl",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);
        var resolved = (string)method!.Invoke(null, [configuredLoginUrl])!;
        Assert.AreEqual("https://www.maersk.com/portaluser/login", resolved);
    }

    [TestMethod]
    public void ExplicitWrongPassword_ShouldRemainTerminal()
    {
        var method = typeof(MaerskLoginService).GetMethod(
            "LooksLikeAuthenticationFailure",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);

        var result = (bool)method!.Invoke(
            null,
            ["Incorrect password. Please try again."])!;

        Assert.IsTrue(result);
    }
}
