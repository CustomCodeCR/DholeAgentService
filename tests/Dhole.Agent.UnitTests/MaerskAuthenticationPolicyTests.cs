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
