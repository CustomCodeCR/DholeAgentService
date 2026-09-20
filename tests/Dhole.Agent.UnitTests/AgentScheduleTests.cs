using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class AgentScheduleTests
{
    [TestMethod]
    public void Create_IntervalWithoutPositiveMinutes_ShouldThrow()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            AgentSchedule.Create("rates", Guid.NewGuid(), Guid.NewGuid(), null,
                AgentScheduleType.Interval, null, 0, null, "America/Costa_Rica", "{}", 3, 120));
    }

    [TestMethod]
    public void MarkDispatched_Once_ShouldDeactivateWithoutNextExecution()
    {
        var schedule = AgentSchedule.Create("once", Guid.NewGuid(), Guid.NewGuid(), null,
            AgentScheduleType.Once, null, null, DateTime.UtcNow.AddMinutes(5), "UTC", "{}", 0, 60);

        schedule.MarkDispatched(DateTime.UtcNow, null);

        Assert.IsFalse(schedule.IsActive);
        Assert.IsNotNull(schedule.LastExecutionAt);
    }
}
