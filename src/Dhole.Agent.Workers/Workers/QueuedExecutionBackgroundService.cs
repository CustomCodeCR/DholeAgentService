using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Application.Abstractions.Runtime;

namespace Dhole.Agent.Workers.Workers;

/// <summary>
/// PostgreSQL-backed execution pump. PostgreSQL is authoritative for queued
/// executions; Redis is only an integration transport and is not required to start.
/// </summary>
public sealed class QueuedExecutionBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<QueuedExecutionBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private DateTime _nextHeartbeatAtUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogWarning(
            "AGENT_QUEUE_PUMP_STARTED utc={UtcNow}",
            DateTime.UtcNow);

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessQueuedAsync(stoppingToken);

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ProcessQueuedAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var executions = scope.ServiceProvider.GetRequiredService<IAgentExecutionRepository>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentExecutionOrchestrator>();

            var queued = await executions.GetQueuedOlderThanAsync(
                DateTime.UtcNow,
                100,
                cancellationToken);

            var now = DateTime.UtcNow;
            if (now >= _nextHeartbeatAtUtc)
            {
                logger.LogWarning(
                    "AGENT_QUEUE_PUMP_HEARTBEAT utc={UtcNow} queued={QueuedCount}",
                    now,
                    queued.Count);
                _nextHeartbeatAtUtc = now.AddSeconds(10);
            }

            foreach (var execution in queued)
            {
                cancellationToken.ThrowIfCancellationRequested();

                logger.LogWarning(
                    "AGENT_QUEUE_DISPATCH execution={ExecutionId} status={Status} attempt={Attempt} created={CreatedAtUtc}",
                    execution.Id,
                    execution.Status,
                    execution.Attempt,
                    execution.CreatedAtUtc);

                await orchestrator.ExecuteAsync(execution.Id, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AGENT_QUEUE_PUMP_ERROR");
        }
    }
}
