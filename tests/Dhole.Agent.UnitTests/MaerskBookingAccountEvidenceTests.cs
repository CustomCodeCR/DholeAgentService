using Dhole.Agent.Infrastructure.Providers.Maersk.Authentication;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskBookingAccountEvidenceTests
{
    [DataTestMethod]
    [DataRow("https://www.maersk.com/book/sailings")]
    [DataRow("https://maersk.com/book/sailings")]
    [DataRow("https://www.maersk.com/book/")]
    [DataRow("https://maersk.com/book")]
    public void OnlyOfficialBookingOriginIsEligible(string url)
        => Assert.IsTrue(MaerskBookingAccountEvidence.IsOfficialBookingPage(url));

    [DataTestMethod]
    [DataRow("https://evil-maersk.com/book/sailings")]
    [DataRow("https://maersk.com.evil.example/book")]
    [DataRow("https://accounts.maersk.com/book/sailings")]
    [DataRow("http://www.maersk.com/book")]
    [DataRow("https://www.maersk.com/bookings-fake")]
    [DataRow("https://www.maersk.com/portaluser/login")]
    [DataRow("about:blank")]
    public void OtherPagesNeverCountAsAuthenticatedBooking(string url)
        => Assert.IsFalse(MaerskBookingAccountEvidence.IsOfficialBookingPage(url));

    [DataTestMethod]
    [DataRow("RS", "Royner Sibaja")]
    [DataRow("JL", "John Lopez")]
    [DataRow("AM", "Ana María")]
    public void MatchingUserAvatarAndDisplayNameCountAsIdentity(string initials, string name)
        => Assert.IsTrue(MaerskBookingAccountEvidence.AvatarMatchesDisplayName(initials, name));

    [DataTestMethod]
    [DataRow("EN", "Notifications Support")]
    [DataRow("RS", "Some Other")]
    [DataRow("CU", "Contact Us")]
    [DataRow("AM", "Ask Maersk")]
    [DataRow("MS", "Maersk Spot")]
    [DataRow("SI", "Sign In")]
    [DataRow("LI", "Log In")]
    [DataRow("", "Royner Sibaja")]
    [DataRow("RS", "")]
    [DataRow("rs", "Royner Sibaja")]
    [DataRow("R", "Royner Sibaja")]
    public void NavigationOrMismatchedAvatarNeverCountAsAccount(string initials, string name)
        => Assert.IsFalse(MaerskBookingAccountEvidence.AvatarMatchesDisplayName(initials, name));
}
