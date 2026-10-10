using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Application.Runtime;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.Workers.Workers;

/// <summary>
/// Opt-in health sampler. Every poll owns its own scope and context. Parallel
/// replicas are safe because alert keys are unique and state changes atomic.
/// </summary>
public sealed class MaerskMonitoringBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<MaerskMonitoringOptions> configured,
    ILogger<MaerskMonitoringBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configured.Value;
        if (!options.Enabled)
        {
            logger.LogInformation("MAERSK_MONITOR_DISABLED");
            return;
        }
        options.Validate();
        logger.LogInformation("MAERSK_MONITOR_ENABLED intervalSeconds={Interval}",
            options.PollIntervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollIntervalSeconds));
        try
        {
            // Evaluate immediately on startup to surface preexisting problems.
            do
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var monitor = scope.ServiceProvider.GetRequiredService<IMaerskMonitoring>();
                    await monitor.EvaluateAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MAERSK_MONITOR_SAMPLE_FAILED");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
