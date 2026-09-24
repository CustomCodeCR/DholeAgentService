using CustomCodeFramework.Redis.Abstractions;
using CustomCodeFramework.Workers.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Application.Abstractions.Runtime;

namespace Dhole.Agent.Workers.Workers;

public sealed class QueuedExecutionRecoveryWorker(
    IAgentExecutionRepository executions,
    IAgentExecutionOrchestrator orchestrator,
    IDistributedLock distributedLock,
    ILogger<QueuedExecutionRecoveryWorker> logger) : IBackgroundWorker
{
    private static readonly TimeSpan QueueGracePeriod = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExecutionLockDuration = TimeSpan.FromMinutes(10);

    public string Name => "agent-queued-execution-recovery";

    public async Task ExecuteAsync(
        IWorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
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
                "Recovering queued execution {ExecutionId} created at {CreatedAtUtc}; Redis event was not consumed in time.",
                execution.Id,
                execution.CreatedAtUtc);

            await orchestrator.ExecuteAsync(execution.Id, cancellationToken);
        }
    }
}
