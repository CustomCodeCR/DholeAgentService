using Cronos;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Workers.Scheduling;

public sealed class ScheduleCalculator
{
    public DateTime? GetInitial(AgentSchedule schedule,DateTime utcNow)
        => schedule.ScheduleType switch
        {
            AgentScheduleType.Once => schedule.ExecuteAt,
            AgentScheduleType.Interval => utcNow,
            AgentScheduleType.Cron => NextCron(schedule,utcNow),
            _ => null
        };

    public DateTime? GetNext(AgentSchedule schedule,DateTime utcNow)
        => schedule.ScheduleType switch
        {
            AgentScheduleType.Once => null,
            AgentScheduleType.Interval => utcNow.AddMinutes(schedule.IntervalMinutes!.Value),
            AgentScheduleType.Cron => NextCron(schedule,utcNow),
            _ => null
        };

    private static DateTime? NextCron(AgentSchedule schedule,DateTime utcNow)
    {
        var expression=CronExpression.Parse(schedule.CronExpression!,CronFormat.Standard);
        var timezone=TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone);
        return expression.GetNextOccurrence(DateTime.SpecifyKind(utcNow,DateTimeKind.Utc),timezone,inclusive:false);
    }
}
