using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Workers.Scheduling;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class ScheduleCalculatorTests
{
    [TestMethod]
    public void GetNext_Interval_ShouldAdvanceConfiguredMinutes()
    {
        var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var schedule = AgentSchedule.Create(
            "interval",
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            null,
            AgentScheduleType.Interval,
            null,
            30,
            null,
            "UTC",
            "{}",
            3,
            120);

        var next = new ScheduleCalculator().GetNext(schedule, now);

        Assert.AreEqual(now.AddMinutes(30), next);
    }

    [TestMethod]
    public void GetNext_Cron_ShouldCalculateNextOccurrence()
    {
        var now = new DateTime(2026, 9, 20, 12, 15, 0, DateTimeKind.Utc);
        var schedule = AgentSchedule.Create(
            "cron",
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            null,
            AgentScheduleType.Cron,
            "0 13 * * *",
            null,
            null,
            "UTC",
            "{}",
            3,
            120);

        var next = new ScheduleCalculator().GetNext(schedule, now);

        Assert.AreEqual(new DateTime(2026, 9, 20, 13, 0, 0, DateTimeKind.Utc), next);
    }

    [TestMethod]
    public void GetNext_CronCostaRica6Pm_ShouldUseConfiguredTimezone()
    {
        var now = new DateTime(2026, 9, 25, 22, 0, 0, DateTimeKind.Utc);
        var schedule = AgentSchedule.Create(
            "daily-6pm",
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            null,
            AgentScheduleType.Cron,
            "0 18 * * *",
            null,
            null,
            "America/Costa_Rica",
            "{}",
            2,
            600);

        var next = new ScheduleCalculator().GetNext(schedule, now);

        Assert.AreEqual(
            new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc),
            next);
    }

    [TestMethod]
    public void GetNext_Once_ShouldReturnNull()
    {
        var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var schedule = AgentSchedule.Create(
            "once",
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            null,
            AgentScheduleType.Once,
            null,
            null,
            now.AddHours(1),
            "UTC",
            "{}",
            0,
            120);

        Assert.IsNull(new ScheduleCalculator().GetNext(schedule, now));
    }
}
