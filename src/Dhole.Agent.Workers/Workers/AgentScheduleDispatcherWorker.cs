using CustomCodeFramework.Persistence.Abstractions;
using CustomCodeFramework.Redis.Abstractions;
using CustomCodeFramework.Workers.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Workers.Scheduling;

namespace Dhole.Agent.Workers.Workers;

public sealed class AgentScheduleDispatcherWorker(
    IAgentScheduleRepository schedules,
    IAgentExecutionRepository executions,
    IUnitOfWork unitOfWork,
    IDistributedLock distributedLock,
    ScheduleCalculator calculator,
    ILogger<AgentScheduleDispatcherWorker> logger
):IBackgroundWorker
{
    public string Name=>"agent-schedule-dispatcher";

    public async Task ExecuteAsync(IWorkerExecutionContext context,CancellationToken cancellationToken)
    {
        var now=DateTime.UtcNow;
        var all=await schedules.GetAllAsync(cancellationToken);
        foreach(var snapshot in all.Where(x=>x.IsActive))
        {
            var dueAt=snapshot.NextExecutionAt??calculator.GetInitial(snapshot,now);
            if(!dueAt.HasValue||dueAt.Value>now)continue;

            await using var handle=await distributedLock.AcquireAsync($"agent:schedule:{snapshot.Id}",TimeSpan.FromSeconds(120),cancellationToken);
            if(handle is null)continue;

            var schedule=await schedules.GetByIdAsync(snapshot.Id,cancellationToken);
            if(schedule is null||schedule.IsDeleted||!schedule.IsActive)continue;

            var effectiveDue=schedule.NextExecutionAt??calculator.GetInitial(schedule,now);
            if(!effectiveDue.HasValue||effectiveDue.Value>now)continue;

            var execution=AgentExecution.Create(
                schedule.AgentDefinitionId,
                schedule.ProviderId,
                schedule.Id,
                schedule.CredentialId,
                AgentExecutionType.Scheduled,
                0,
                schedule.InputJson,
                schedule.MaxRetries+1,
                Guid.NewGuid().ToString("N"));
            execution.Queue();

            await executions.AddAsync(execution,cancellationToken);
            var next=calculator.GetNext(schedule,now);
            schedule.MarkDispatched(now,next);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Dispatched schedule {ScheduleId} as execution {ExecutionId}.",schedule.Id,execution.Id);
        }
    }
}
