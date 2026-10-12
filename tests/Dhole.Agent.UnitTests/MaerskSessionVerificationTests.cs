using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskSessionVerificationTests
{
    [TestMethod]
    public void OnlyExactManualAuthenticateActionCanUseVerificationGate()
    {
        var provider = AgentProvider.Create("MAERSK", "Maersk",
            AgentProviderType.Maersk, null,
            AgentExecutionStrategy.BrowserNetworkCapture, true, null);
        var definition = AgentDefinition.Create(provider.Id,
            MaerskSessionVerification.DefinitionCode, "Verify original session", null,
            AgentActionType.Authenticate, AgentExecutionStrategy.BrowserNetworkCapture, null);

        AgentExecution Make(AgentExecutionType type, string input, int attempts = 1)
            => AgentExecution.Create(definition.Id, provider.Id,
                null, Guid.NewGuid(), type, 1000, input, attempts,
                Guid.NewGuid().ToString("N"));

        Assert.IsTrue(MaerskSessionVerification.IsExplicit(
            Make(AgentExecutionType.Manual, MaerskSessionVerification.InputJson),
            definition, provider));
        Assert.IsFalse(MaerskSessionVerification.IsExplicit(
            Make(AgentExecutionType.Scheduled, MaerskSessionVerification.InputJson),
            definition, provider));
        Assert.IsFalse(MaerskSessionVerification.IsExplicit(
            Make(AgentExecutionType.Manual, MaerskSessionVerification.InputJson, 3),
            definition, provider));
        Assert.IsFalse(MaerskSessionVerification.IsExplicit(
            Make(AgentExecutionType.Manual, "{\"commodity\":\"TIRES\"}"),
            definition, provider));

        var search = AgentDefinition.Create(provider.Id, "MAERSK_RATES", "Rates", null,
            AgentActionType.SearchOceanRates, AgentExecutionStrategy.BrowserNetworkCapture, null);
        var forged = AgentExecution.Create(search.Id, provider.Id, null, Guid.NewGuid(),
            AgentExecutionType.Manual, 1000, MaerskSessionVerification.InputJson, 1, Guid.NewGuid().ToString("N"));
        Assert.IsFalse(MaerskSessionVerification.IsExplicit(forged, search, provider));
    }

    [TestMethod]
    public void ProviderCircuitStaysClosedToNormalJobsUntilVerifiedReset()
    {
        var state = new MaerskCircuitSnapshot(Guid.NewGuid(), "Open", true,
            "maersk_authentication_edge_denied", DateTime.UtcNow.AddHours(-2), 1, null);
        Assert.IsFalse(state.CanSchedule(DateTime.UtcNow));
    }
}
