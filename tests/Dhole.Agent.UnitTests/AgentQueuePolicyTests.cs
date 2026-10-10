using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class AgentQueuePolicyTests
{
    [TestMethod]
    public void TechnicalRetry_PreservesExecutionIdentitySnapshotAndCorrelation()
    {
        var execution = Create(maxAttempts: 3);
        execution.Queue();
        execution.AttachProfileSnapshot(Guid.NewGuid(), "Original prompt", """{"a":1}""");
        var originalId = execution.Id;
        var correlationId = execution.CorrelationId;
        var originalInput = execution.InputJson;
        var snapshotId = execution.ExtractionProfileId;

        execution.Start(DateTime.UtcNow);
        var retryAt = DateTime.UtcNow.AddSeconds(15);
        execution.QueueTransientRetry("maersk_chromium_launch_failed", "Transient local browser error", retryAt);

        Assert.AreEqual(AgentExecutionStatus.Queued, execution.Status);
        Assert.AreEqual(retryAt, execution.NextAttemptAtUtc);
        Assert.AreEqual(1, execution.Attempt);
        Assert.AreEqual(originalId, execution.Id);
        Assert.AreEqual(correlationId, execution.CorrelationId);
        Assert.AreEqual(originalInput, execution.InputJson);
        Assert.AreEqual(snapshotId, execution.ExtractionProfileId);
        Assert.AreEqual("Original prompt", execution.PromptSnapshot);
        Assert.AreEqual("""{"a":1}""", execution.ConfigurationSnapshotJson);

        execution.Start(DateTime.UtcNow);
        Assert.AreEqual(2, execution.Attempt);
        Assert.IsNull(execution.NextAttemptAtUtc);
    }

    [TestMethod]
    public void ExhaustedAttempts_CannotStartOrRetry()
    {
        var execution = Create(maxAttempts: 1);
        execution.Queue();
        execution.Start(DateTime.UtcNow);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            execution.QueueTransientRetry("maersk_browser_profile_busy", "busy", DateTime.UtcNow));

        // Only a controlled retry transition returns to Queued.
        execution.Fail("test", "one attempt exhausted", DateTime.UtcNow);
        Assert.AreEqual(AgentExecutionStatus.Failed, execution.Status);
    }

    [TestMethod]
    public void RetryRequiresRunningStateAndUtcDelay()
    {
        var execution = Create(maxAttempts: 3);
        execution.Queue();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            execution.QueueTransientRetry("transient", "error", DateTime.UtcNow.AddSeconds(15)));

        execution.Start(DateTime.UtcNow);
        Assert.ThrowsExactly<ArgumentException>(() =>
            execution.QueueTransientRetry("transient", "error", DateTime.Now.AddSeconds(15)));
    }

    [TestMethod]
    public void MaxAttempts_AreEnforcedEvenWhenResumeIsRequested()
    {
        var execution = Create(maxAttempts: 1);
        execution.Queue();
        execution.Start(DateTime.UtcNow);
        execution.WaitForAuthentication("maersk_hcaptcha_required", "verification");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            execution.Start(DateTime.UtcNow));
        Assert.AreEqual(AgentExecutionStatus.WaitingForAuthentication, execution.Status);
    }

    [TestMethod]
    public void DefaultQueueOptions_AreDisabledAndConservativelyBounded()
    {
        var options = new AgentQueueOptions();
        options.Validate();
        Assert.IsFalse(options.ConcurrentDispatcherEnabled);
        Assert.AreEqual(1, options.MaxConcurrentMaersk);
        Assert.AreEqual(1, options.MaxConcurrentPerBrowserProfile);
        Assert.AreEqual(180, options.LeaseSeconds);
        Assert.AreEqual(20, options.HeartbeatSeconds);
        Assert.AreEqual(15, options.RetryDelayForAttempt(1));
        Assert.AreEqual(60, options.RetryDelayForAttempt(2));
    }

    [DataTestMethod]
    [DataRow(59, 20)]
    [DataRow(180, 61)]
    [DataRow(180, 0)]
    public void UnsafeLeaseOrHeartbeatSettings_AreRejected(int lease, int heartbeat)
    {
        var options = new AgentQueueOptions { LeaseSeconds = lease, HeartbeatSeconds = heartbeat };
        Assert.ThrowsExactly<InvalidOperationException>(() => options.Validate());
    }

    private static AgentExecution Create(int maxAttempts)
        => AgentExecution.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(),
            AgentExecutionType.Scheduled, 0, """{"route":"Shanghai-Caldera"}""",
            maxAttempts, Guid.NewGuid().ToString("N"));
}
