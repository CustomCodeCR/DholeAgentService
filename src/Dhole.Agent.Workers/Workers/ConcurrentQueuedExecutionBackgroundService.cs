using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.Workers.Workers;

/// <summary>
/// Opt-in bounded dispatcher. Every execution has its own DI scope, PostgreSQL
/// claim and heartbeat. PostgreSQL is authoritative, including when Redis fails.
/// </summary>
public sealed class ConcurrentQueuedExecutionBackgroundService(
    IServiceScopeFactory scopes,
    IOptions<AgentQueueOptions> configured,
    ILogger<ConcurrentQueuedExecutionBackgroundService> logger) : BackgroundService
{
    private readonly AgentQueueOptions _options = configured.Value;
    private readonly Dictionary<Guid, ActiveTask> _active = new();
    private DateTime _lastReconciliation = DateTime.MinValue;

    private sealed record ActiveTask(Task Work, bool IsMaersk);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();
        logger.LogInformation(
            "AGENT_QUEUE_CONCURRENT_STARTED concurrency={Concurrent} maersk={Maersk} lease={Lease} heartbeat={Heartbeat}",
            _options.MaxConcurrentExecutions, _options.MaxConcurrentMaersk,
            _options.LeaseSeconds, _options.HeartbeatSeconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var completed in _active.Where(x => x.Value.Work.IsCompleted).ToArray())
                {
                    _active.Remove(completed.Key);
                    try { await completed.Value.Work; }
                    catch (Exception ex) { logger.LogError(ex, "AGENT_QUEUE_JOB_FAILED execution={ExecutionId}", completed.Key); }
                }

                try { await DispatchBatchAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "AGENT_QUEUE_DISPATCH_ERROR"); }

                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown stops leases/heartbeats and cancels browser work.
        }
        finally
        {
            if (_active.Count > 0)
            {
                try { await Task.WhenAll(_active.Values.Select(x => x.Work)); }
                catch (Exception ex) { logger.LogWarning(ex, "AGENT_QUEUE_SHUTDOWN_WORK_FAILED"); }
            }
        }
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        if (_active.Count >= _options.MaxConcurrentExecutions)
            return;

        using var scope = scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAgentExecutionRepository>();
        var providers = scope.ServiceProvider.GetRequiredService<IAgentProviderRepository>();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IAgentQueueLeaseStore>();
        var maerskCircuit = scope.ServiceProvider.GetRequiredService<IMaerskCircuitBreaker>();

        // Reconciliation never automatically repeats an execution which may
        // have submitted a booking/search or persisted partial results.
        if (DateTime.UtcNow >= _lastReconciliation)
        {
            var interrupted = await leaseStore.FailInterruptedAsync(cancellationToken);
            var exhausted = await leaseStore.FailAttemptsExhaustedAsync(cancellationToken);
            if (interrupted + exhausted > 0)
                logger.LogWarning("AGENT_QUEUE_RECONCILED interrupted={Interrupted} exhausted={Exhausted}",
                    interrupted, exhausted);
            _lastReconciliation = DateTime.UtcNow.AddSeconds(30);
        }

        var candidates = await repository.GetDispatchCandidatesAsync(
            DateTime.UtcNow, 100, cancellationToken);

        foreach (var execution in candidates)
        {
            if (_active.Count >= _options.MaxConcurrentExecutions)
                break;
            if (_active.ContainsKey(execution.Id))
                continue;

            var provider = await providers.GetByIdAsync(execution.ProviderId, cancellationToken);
            if (provider is null)
                continue;
            var isMaersk = provider.Code.Equals("MAERSK", StringComparison.OrdinalIgnoreCase);
            if (isMaersk && _active.Values.Count(x => x.IsMaersk) >= _options.MaxConcurrentMaersk)
                continue;
            if (isMaersk && !await maerskCircuit.CanScheduleAsync(provider.Id, cancellationToken))
                continue;

            // Shared browser profiles are serialized across all workers.
            var scopeKey = isMaersk
                ? $"maersk:{execution.ProviderId:N}"
                : execution.CredentialId is Guid credentialId
                    ? $"profile:{execution.ProviderId:N}:{credentialId:N}"
                    : execution.ExtractionProfileId is Guid profileId
                        ? $"profile:{execution.ProviderId:N}:{profileId:N}"
                        : $"execution:{execution.Id:N}";

            // Do not await provider/browser work inside this loop.
            _active.Add(execution.Id, new ActiveTask(
                RunClaimedAsync(execution.Id, scopeKey, cancellationToken), isMaersk));
        }
    }

    private async Task RunClaimedAsync(
        Guid executionId, string scopeKey, CancellationToken stoppingToken)
    {
        var ownerId = Guid.NewGuid();
        var lease = TimeSpan.FromSeconds(_options.LeaseSeconds);
        var claimed = false;

        try
        {
            using (var claimScope = scopes.CreateScope())
            {
                var store = claimScope.ServiceProvider.GetRequiredService<IAgentQueueLeaseStore>();
                claimed = await store.TryClaimAsync(
                    executionId, scopeKey, ownerId, lease, stoppingToken);
            }
            if (!claimed)
                return;

            logger.LogInformation("AGENT_QUEUE_CLAIMED execution={ExecutionId} scope={Scope}",
                executionId, scopeKey);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            linked.CancelAfter(TimeSpan.FromSeconds(_options.MaxExecutionSeconds));
            var heartbeat = HeartbeatAsync(scopeKey, executionId, ownerId, lease, linked);
            try
            {
                using var taskScope = scopes.CreateScope();
                var orchestrator = taskScope.ServiceProvider
                    .GetRequiredService<IAgentExecutionOrchestrator>();

                // This scope owns its DbContext; the heartbeat owns a separate
                // scope/DbContext on every tick.
                await orchestrator.ExecuteAsync(executionId, linked.Token);
            }
            finally
            {
                await linked.CancelAsync();
                try { await heartbeat; }
                catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("AGENT_QUEUE_CANCELLED execution={ExecutionId}", executionId);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("AGENT_QUEUE_LEASE_LOST execution={ExecutionId}", executionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AGENT_QUEUE_EXECUTION_ERROR execution={ExecutionId}", executionId);
        }
        finally
        {
            if (claimed)
            {
                try
                {
                    using var releaseScope = scopes.CreateScope();
                    var repository = releaseScope.ServiceProvider.GetRequiredService<IAgentExecutionRepository>();
                    var state = await repository.GetByIdAsync(executionId, CancellationToken.None);
                    // Cancellation or timeout can leave a Running row after the
                    // orchestration stops. Keep its lease for expiration-based
                    // reconciliation, never erase the ownership evidence here.
                    if (state?.Status != AgentExecutionStatus.Running)
                    {
                        await releaseScope.ServiceProvider.GetRequiredService<IAgentQueueLeaseStore>()
                            .ReleaseAsync(scopeKey, executionId, ownerId, CancellationToken.None);
                    }
                    else
                    {
                        logger.LogWarning(
                            "AGENT_QUEUE_INTERRUPTED_AWAITING_LEASE_EXPIRY execution={ExecutionId}",
                            executionId);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "AGENT_QUEUE_RELEASE_FAILED execution={ExecutionId}", executionId);
                    // Lease expires and the reconciliation pass handles a
                    // potentially interrupted Running state without replay.
                }
            }
        }
    }

    private async Task HeartbeatAsync(string scopeKey, Guid executionId, Guid ownerId,
        TimeSpan lease, CancellationTokenSource linked)
    {
        while (!linked.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.HeartbeatSeconds), linked.Token);
                using var scope = scopes.CreateScope();
                var renewed = await scope.ServiceProvider.GetRequiredService<IAgentQueueLeaseStore>()
                    .RenewAsync(scopeKey, executionId, ownerId, lease, linked.Token);
                if (!renewed)
                {
                    logger.LogError("AGENT_QUEUE_LEASE_LOST execution={ExecutionId}", executionId);
                    await linked.CancelAsync();
                    break;
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "AGENT_QUEUE_HEARTBEAT_FAILED execution={ExecutionId}", executionId);
                await linked.CancelAsync();
                break;
            }
        }
    }
}
