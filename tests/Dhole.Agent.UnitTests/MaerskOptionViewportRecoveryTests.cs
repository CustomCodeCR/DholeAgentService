using Dhole.Agent.Infrastructure.Providers.Maersk.Browser;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskOptionViewportRecoveryTests
{
    [DataTestMethod]
    [DataRow("Timeout 1500ms exceeded. element is outside of the viewport")]
    [DataRow("Element is outside the viewport")]
    [DataRow("Element is not in the viewport")]
    public void OffscreenElementCanUseExactOptionKeyboardFallback(string message)
        => Assert.IsTrue(MaerskOptionViewportRecovery.IsOutsideViewport(message));

    [DataTestMethod]
    [DataRow("maersk_authentication_edge_denied")]
    [DataRow("hCaptcha challenge required")]
    [DataRow("Timeout 90000ms waiting for departures/offers response")]
    [DataRow("Another element intercepts pointer events")]
    [DataRow("")]
    public void AuthenticationAndOtherFailuresNeverUseViewportFallback(string message)
        => Assert.IsFalse(MaerskOptionViewportRecovery.IsOutsideViewport(message));

    [TestMethod]
    public void MissingPlaywrightMessageDoesNotTriggerKeyboardFallback()
        => Assert.IsFalse(MaerskOptionViewportRecovery.IsOutsideViewport(null));
}
