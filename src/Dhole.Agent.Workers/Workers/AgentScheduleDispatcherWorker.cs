using System.Text.Json;
using CustomCodeFramework.Persistence.Abstractions;
using CustomCodeFramework.Redis.Abstractions;
using CustomCodeFramework.Workers.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Application.ExtractionProfiles;
using Dhole.Agent.Workers.Scheduling;

namespace Dhole.Agent.Workers.Workers;

public sealed class AgentScheduleDispatcherWorker(
    IAgentScheduleRepository schedules,
    IAgentExecutionRepository executions,
    IAgentProviderRepository providers,
    IAgentExtractionProfileRepository extractionProfiles,
    IAgentExtractionRouteRepository extractionRoutes,
    IAgentExtractionEquipmentRepository extractionEquipment,
    IAgentExtractionFieldRepository extractionFields,
    IAgentEndpointCaptureRepository endpointCaptures,
    AgentExecutionSnapshotBuilder snapshotBuilder,
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
        foreach(var scheduleSnapshot in all.Where(x=>x.IsActive))
        {
            var dueAt=scheduleSnapshot.NextExecutionAt??calculator.GetInitial(scheduleSnapshot,now);
            if(!dueAt.HasValue||dueAt.Value>now)continue;

            await using var handle=await distributedLock.AcquireAsync($"agent:schedule:{scheduleSnapshot.Id}",TimeSpan.FromSeconds(120),cancellationToken);
            if(handle is null)continue;

            var schedule=await schedules.GetByIdAsync(scheduleSnapshot.Id,cancellationToken);
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
            var profiles = await extractionProfiles.GetAllAsync(cancellationToken);
            var profile = profiles
                .Where(x => x.IsActive && !x.IsDeleted && x.ProviderId == schedule.ProviderId)
                .OrderByDescending(x => x.CredentialId == schedule.CredentialId)
                .FirstOrDefault();

            if (profile is not null)
            {
                var routes = await extractionRoutes.GetByProfileAsync(profile.Id, cancellationToken);
                var equipment = await extractionEquipment.GetByProfileAsync(profile.Id, cancellationToken);
                var fields = await extractionFields.GetByProfileAsync(profile.Id, cancellationToken);
                var captures = await endpointCaptures.GetByProfileAsync(profile.Id, cancellationToken);
                var profileSnapshot = snapshotBuilder.Build(
                    profile,
                    await GetProviderAsync(schedule.ProviderId, cancellationToken),
                    routes,
                    equipment,
                    fields,
                    captures,
                    TryGetCargoReadyDate(schedule.InputJson),
                    execution.Id);
                execution.AttachProfileSnapshot(profile.Id, profileSnapshot.Prompt, profileSnapshot.ConfigurationJson);
            }

            execution.Queue();

            await executions.AddAsync(execution,cancellationToken);
            var next=calculator.GetNext(schedule,now);
            schedule.MarkDispatched(now,next);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Dispatched schedule {ScheduleId} as execution {ExecutionId}.",schedule.Id,execution.Id);
        }
    }
    private async Task<AgentProvider> GetProviderAsync(Guid providerId, CancellationToken cancellationToken)
        => await providers.GetByIdAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Agent provider {providerId} not found while dispatching schedule.");

    private static DateOnly? TryGetCargoReadyDate(string inputJson)
    {
        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (!document.RootElement.TryGetProperty("cargoReadyDate", out var value) || value.ValueKind != JsonValueKind.String)
                return null;
            return DateOnly.TryParse(value.GetString(), out var date) ? date : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
