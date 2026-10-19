using Dhole.Agent.Persistence.Repositories;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskGenerationProfileScopeTests
{
    [DataTestMethod]
    [DataRow("prod1", "maersk-prod1-")]
    [DataRow("prod2", "maersk-prod2-")]
    [DataRow("prod12", "maersk-prod12-")]
    public void ValidGenerationUsesItsOwnProfilePrefix(string generation, string expected)
        => Assert.AreEqual(expected, MaerskGenerationProfileScope.Prefix(generation));

    [DataTestMethod]
    [DataRow("prod0")]
    [DataRow("prod01")]
    [DataRow("prod1/../prod2")]
    [DataRow("prod2;drop")]
    [DataRow("")]
    public void InvalidGenerationCannotSelectAnotherProfile(string generation)
        => Assert.ThrowsExactly<ArgumentException>(() =>
            MaerskGenerationProfileScope.Prefix(generation));
}
