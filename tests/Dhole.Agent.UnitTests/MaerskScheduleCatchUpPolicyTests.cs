using Dhole.Agent.Workers.Scheduling;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskScheduleCatchUpPolicyTests
{
    private static readonly DateTime Now =
        new(2026, 10, 18, 21, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void BlockedProvider_SkipsDueOccurrenceEvenWithinNormalGrace()
    {
        Assert.IsTrue(MaerskScheduleCatchUpPolicy.ShouldSkip(
            Now, Now, providerBlocked: true));
        Assert.IsTrue(MaerskScheduleCatchUpPolicy.ShouldSkip(
            Now.AddSeconds(-5), Now, providerBlocked: true));
    }

    [TestMethod]
    public void ReopenedProvider_DoesNotReplayOverdueSchedule()
    {
        Assert.IsTrue(MaerskScheduleCatchUpPolicy.ShouldSkip(
            Now.AddMinutes(-53), Now, providerBlocked: false));
        Assert.IsTrue(MaerskScheduleCatchUpPolicy.ShouldSkip(
            Now.AddMinutes(-2), Now, providerBlocked: false));
    }

    [TestMethod]
    public void OrdinaryDispatcherJitter_DoesNotSkipCurrentOccurrence()
    {
        Assert.IsFalse(MaerskScheduleCatchUpPolicy.ShouldSkip(
            Now, Now, providerBlocked: false));
        Assert.IsFalse(MaerskScheduleCatchUpPolicy.ShouldSkip(
            Now.AddSeconds(-30), Now, providerBlocked: false));
        Assert.IsFalse(MaerskScheduleCatchUpPolicy.ShouldSkip(
            Now.AddMinutes(-1), Now, providerBlocked: false));
    }

    [TestMethod]
    public void AdvanceOnlyToFutureRecurringOccurrence()
    {
        var interval = Dhole.Agent.Domain.Agents.AgentSchedule.Create(
            "interval", Guid.NewGuid(), Guid.NewGuid(), null, null,
            Dhole.Agent.Domain.Agents.AgentScheduleType.Interval, null, 30, null,
            "UTC", "{}", 0, 60);
        var cron = Dhole.Agent.Domain.Agents.AgentSchedule.Create(
            "cron", Guid.NewGuid(), Guid.NewGuid(), null, null,
            Dhole.Agent.Domain.Agents.AgentScheduleType.Cron, "0 * * * *", null, null,
            "UTC", "{}", 0, 60);
        var calculator = new ScheduleCalculator();
        var intervalNext = calculator.GetNext(interval, Now);
        var cronNext = calculator.GetNext(cron, Now);

        Assert.IsTrue(intervalNext > Now);
        Assert.IsTrue(cronNext > Now);
        interval.SkipMissedOccurrence(intervalNext);
        cron.SkipMissedOccurrence(cronNext);
        Assert.AreEqual(intervalNext, interval.NextExecutionAt);
        Assert.AreEqual(cronNext, cron.NextExecutionAt);
        Assert.IsNull(interval.LastExecutionAt);
        Assert.IsNull(cron.LastExecutionAt);
    }
}
