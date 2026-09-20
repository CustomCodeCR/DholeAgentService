using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class AgentExecutionTests
{
    [TestMethod]
    public void Complete_ShouldRequireRunningExecution()
    {
        var execution = Create();

        Assert.ThrowsExactly<InvalidOperationException>(() => execution.Complete("{}", DateTime.UtcNow));
    }

    [TestMethod]
    public void StartAndComplete_ShouldTrackStatusAndAttempt()
    {
        var execution = Create();
        var started = DateTime.UtcNow;

        execution.Queue();
        execution.Start(started);
        execution.Complete("{\"ok\":true}", started.AddSeconds(2));

        Assert.AreEqual(AgentExecutionStatus.Completed, execution.Status);
        Assert.AreEqual(1, execution.Attempt);
        Assert.AreEqual(2000L, execution.DurationMs);
    }

    private static AgentExecution Create() =>
        AgentExecution.Create(Guid.NewGuid(), Guid.NewGuid(), null, null, AgentExecutionType.Manual,
            0, "{}", 3, Guid.NewGuid().ToString("N"));
}
