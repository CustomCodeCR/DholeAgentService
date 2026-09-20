using CustomCodeFramework.Messaging.DependencyInjection;
using CustomCodeFramework.Messaging.Outbox.DependencyInjection;
using CustomCodeFramework.Redis.DependencyInjection;
using CustomCodeFramework.Redis.Streams.DependencyInjection;
using CustomCodeFramework.Workers.DependencyInjection;
using Dhole.Agent.Workers.Outbox;
using Dhole.Agent.Workers.Streams;
using Dhole.Agent.Workers.Scheduling;
using Dhole.Agent.Workers.Workers;

namespace Dhole.Agent.Workers.DependencyInjection;

public static class WorkerServiceCollectionExtensions
{
    public static IServiceCollection AddAgentWorker(this IServiceCollection services,IConfiguration configuration)
    {
        services.AddCustomCodeRedis(configuration);
        services.AddCustomCodeRedisStreams(configuration);
        services.AddCustomCodeMessaging(configuration);
        services.AddCustomCodeMessagingOutbox(configuration);
        services.AddCustomCodeOutboxProcessor<OutboxProcessor>();
        services.AddCustomCodeInboxProcessor<InboxProcessor>();
        services.AddCustomCodeMessagingOutboxHostedServices();
        services.AddCustomCodeRedisStreamConsumerBackgroundService();
        services.AddCustomCodeRedisStreamHandler<AgentExecutionRequestedStreamHandler>();
        services.AddCustomCodeWorkers(configuration);
        services.AddSingleton<ScheduleCalculator>();
        services.AddCustomCodePeriodicWorker<AgentScheduleDispatcherWorker>();
        return services;
    }
}
