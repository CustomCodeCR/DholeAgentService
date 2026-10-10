using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.UnitTests;

/// <summary>
/// Phase 7: automated policy regression matrix, no provider traffic.
/// </summary>
[TestClass]
public sealed class MaerskPhase7SafetyMatrixTests
{
    [DataTestMethod]
    [DataRow("maersk_hcaptcha_required", FailureCategory.ProviderVerificationRequired)]
    [DataRow("maersk_authentication_verification_required", FailureCategory.ProviderVerificationRequired)]
    [DataRow("maersk_authentication_edge_denied", FailureCategory.ProviderAccessRestricted)]
    [DataRow("maersk_authentication_forbidden", FailureCategory.ProviderAccessRestricted)]
    [DataRow("maersk_authentication_rate_limited", FailureCategory.ProviderRateLimited)]
    [DataRow("maersk_browser_profile_blocked", FailureCategory.ProviderVerificationRequired)]
    public void ProviderRestriction_NeverRetriesAutomaticallyOrChangesIdentity(
        string code, FailureCategory category)
    {
        var result = MaerskFailureClassifier.Classify(code);
        Assert.AreEqual(category, result.Category);
        Assert.IsTrue(result.PreserveProfile);
        Assert.IsTrue(result.RequiresOperator);
        Assert.IsTrue(result.ShouldOpenCircuit);
        Assert.IsFalse(result.CanRetry);
    }

    [DataTestMethod]
    [DataRow(403, FailureCategory.ProviderAccessRestricted)]
    [DataRow(429, FailureCategory.ProviderRateLimited)]
    public void TrustedHttpRestrictionCannotCauseBrowserRetry(
        int httpStatus, FailureCategory category)
    {
        var classification = MaerskFailureClassifier.Classify(null, httpStatus);
        Assert.AreEqual(category, classification.Category);
        Assert.IsTrue(classification.ShouldOpenCircuit);
        Assert.IsTrue(classification.RequiresOperator);
        Assert.IsFalse(classification.CanRetry);
        Assert.IsTrue(classification.PreserveProfile);
    }

    [DataTestMethod]
    [DataRow("maersk_chromium_launch_failed")]
    [DataRow("maersk_profile_lock_unavailable")]
    [DataRow("maersk_worker_connection_failed")]
    public void LocalInfrastructureFailures_PreserveProfileAndAllowOnlyBoundedRetry(
        string code)
    {
        var result = MaerskFailureClassifier.Classify(code);
        Assert.AreEqual(FailureCategory.InfrastructureTransient, result.Category);
        Assert.IsTrue(result.CanRetry);
        Assert.IsTrue(result.PreserveProfile);
        Assert.IsFalse(result.RequiresOperator);
        Assert.IsFalse(result.ShouldOpenCircuit);
    }

    [DataTestMethod]
    [DataRow("maersk_all_searches_failed")]
    [DataRow("invalid_input")]
    [DataRow("missing_credential")]
    [DataRow("unrecognized_exception")]
    public void UnknownAggregateAndValidationErrors_NeverAutomaticallyRetry(string code)
    {
        var result = MaerskFailureClassifier.Classify(code);
        Assert.IsFalse(result.CanRetry);
        Assert.IsTrue(result.PreserveProfile);
    }

    [TestMethod]
    public void UnverifiedWaitForAuthentication_DoesNotTransitionToQueued()
    {
        var execution = AgentExecution.Create(Guid.NewGuid(), Guid.NewGuid(),
            null, null, AgentExecutionType.Scheduled, 0,
            """{"route":"Shanghai-Caldera"}""", 2, Guid.NewGuid().ToString("N"));
        var identity = execution.Id;
        var input = execution.InputJson;
        execution.Queue();
        execution.Start(DateTime.UtcNow);
        execution.WaitForAuthentication("maersk_hcaptcha_required", "Manual verification required");
        Assert.AreEqual(AgentExecutionStatus.WaitingForAuthentication, execution.Status);
        Assert.AreEqual(1, execution.Attempt);
        Assert.IsNull(execution.NextAttemptAtUtc);
        Assert.AreEqual(identity, execution.Id);
        Assert.AreEqual(input, execution.InputJson);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            execution.QueueTransientRetry("maersk_chromium_launch_failed", "invalid", DateTime.UtcNow));
    }

    [TestMethod]
    public void FlagsRemainOffByDefaultUntilStagingAcceptanceAndRollout()
    {
        Assert.IsFalse(new MaerskCircuitOptions().Enabled);
        Assert.IsFalse(new AgentQueueOptions().ConcurrentDispatcherEnabled);
        Assert.IsFalse(new MaerskMonitoringOptions().Enabled);
    }
}
