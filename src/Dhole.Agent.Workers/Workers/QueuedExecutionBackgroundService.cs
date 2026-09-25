using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Application.Abstractions.Runtime;

namespace Dhole.Agent.Workers.Workers;

/// <summary>
/// Durable database-backed execution pump. PostgreSQL is the source of truth for
/// queued executions; Redis is not required for an execution to start.
/// </summary>
public sealed class QueuedExecutionBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<QueuedExecutionBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogWarning("Queued execution database pump started. PostgreSQL queue is authoritative.");

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
                25,
                cancellationToken);

            if (queued.Count > 0)
            {
                logger.LogWarning(
                    "Database pump found {QueuedCount} queued execution(s).",
                    queued.Count);
            }

            foreach (var execution in queued)
            {
                cancellationToken.ThrowIfCancellationRequested();

                logger.LogWarning(
                    "Starting queued execution {ExecutionId} directly from PostgreSQL. CreatedAtUtc={CreatedAtUtc}.",
                    execution.Id,
                    execution.CreatedAtUtc);

                await orchestrator.ExecuteAsync(execution.Id, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Queued execution database pump failed.");
        }
    }
}
