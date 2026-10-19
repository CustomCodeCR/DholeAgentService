using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class AgentScheduleTests
{
    [TestMethod]
    public void Create_IntervalWithoutPositiveMinutes_ShouldThrow()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            AgentSchedule.Create("rates", Guid.NewGuid(), Guid.NewGuid(), null, null,
                AgentScheduleType.Interval, null, 0, null, "America/Costa_Rica", "{}", 3, 120));
    }

    [TestMethod]
    public void SkipMissedOccurrence_Once_DeactivatesWithoutClaimingExecution()
    {
        var due = new DateTime(2026, 10, 18, 20, 0, 0, DateTimeKind.Utc);
        var schedule = AgentSchedule.Create("once", Guid.NewGuid(), Guid.NewGuid(), null, null,
            AgentScheduleType.Once, null, null, due, "UTC", "{}", 0, 60);

        schedule.SkipMissedOccurrence(null);

        Assert.IsFalse(schedule.IsActive);
        Assert.IsNull(schedule.NextExecutionAt);
        Assert.IsNull(schedule.LastExecutionAt);
    }

    [TestMethod]
    public void SkipMissedOccurrence_Recurring_PreservesExecutionHistory()
    {
        var due = new DateTime(2026, 10, 18, 20, 0, 0, DateTimeKind.Utc);
        var schedule = AgentSchedule.Create("interval", Guid.NewGuid(), Guid.NewGuid(), null, null,
            AgentScheduleType.Interval, null, 30, null, "UTC", "{}", 0, 60);
        var previous = due.AddHours(-2);
        schedule.MarkDispatched(previous, due);
        var future = due.AddHours(1);

        schedule.SkipMissedOccurrence(future);

        Assert.IsTrue(schedule.IsActive);
        Assert.AreEqual(future, schedule.NextExecutionAt);
        Assert.AreEqual(previous, schedule.LastExecutionAt);
    }

    [TestMethod]
    public void SkipMissedOccurrence_RejectsInvalidNextDateForType()
    {
        var once = AgentSchedule.Create("once", Guid.NewGuid(), Guid.NewGuid(), null, null,
            AgentScheduleType.Once, null, null, DateTime.UtcNow, "UTC", "{}", 0, 60);
        var recurring = AgentSchedule.Create("interval", Guid.NewGuid(), Guid.NewGuid(), null, null,
            AgentScheduleType.Interval, null, 5, null, "UTC", "{}", 0, 60);

        Assert.ThrowsExactly<ArgumentException>(() =>
            once.SkipMissedOccurrence(DateTime.UtcNow.AddMinutes(5)));
        Assert.ThrowsExactly<ArgumentException>(() => recurring.SkipMissedOccurrence(null));
    }

    [TestMethod]
    public void MarkDispatched_Once_ShouldDeactivateWithoutNextExecution()
    {
        var schedule = AgentSchedule.Create("once", Guid.NewGuid(), Guid.NewGuid(), null, null,
            AgentScheduleType.Once, null, null, DateTime.UtcNow.AddMinutes(5), "UTC", "{}", 0, 60);

        schedule.MarkDispatched(DateTime.UtcNow, null);

        Assert.IsFalse(schedule.IsActive);
        Assert.IsNotNull(schedule.LastExecutionAt);
    }
}
