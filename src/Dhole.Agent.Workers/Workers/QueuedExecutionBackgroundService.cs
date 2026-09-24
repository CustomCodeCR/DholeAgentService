using CustomCodeFramework.Redis.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Application.Abstractions.Runtime;

namespace Dhole.Agent.Workers.Workers;

/// <summary>
/// Durable database-backed execution pump. Redis remains the fast path, but queued
/// executions never depend on a Redis Stream delivery in order to start.
/// </summary>
public sealed class QueuedExecutionBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<QueuedExecutionBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QueueGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExecutionLockDuration = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queued execution database pump started.");

        await ProcessQueuedAsync(stoppingToken);

        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProcessQueuedAsync(stoppingToken);
        }
    }

    private async Task ProcessQueuedAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var executions = scope.ServiceProvider.GetRequiredService<IAgentExecutionRepository>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentExecutionOrchestrator>();
            var distributedLock = scope.ServiceProvider.GetRequiredService<IDistributedLock>();

            var cutoff = DateTime.UtcNow.Subtract(QueueGracePeriod);
            var queued = await executions.GetQueuedOlderThanAsync(cutoff, 25, cancellationToken);

            foreach (var execution in queued)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var handle = await distributedLock.AcquireAsync(
                    $"agent:execution:{execution.Id:N}",
                    ExecutionLockDuration,
                    cancellationToken);

                if (handle is null)
                    continue;

                logger.LogWarning(
                    "Starting queued execution {ExecutionId} directly from PostgreSQL fallback. CreatedAtUtc={CreatedAtUtc}.",
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
