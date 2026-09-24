using System.Text.Json;
using CustomCodeFramework.Messaging.Outbox;
using CustomCodeFramework.Messaging.Outbox.Processing;
using CustomCodeFramework.Redis.Streams.Abstractions;
using CustomCodeFramework.Redis.Streams.Messages;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.Workers.Outbox;

internal sealed class OutboxProcessor(
    ServiceDbContext dbContext,
    IRedisStreamPublisher publisher,
    IConfiguration configuration,
    ILogger<OutboxProcessor> logger) : IOutboxProcessor
{
    public async Task<OutboxProcessingResult> ProcessAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var maxRetries = Math.Max(1, configuration.GetValue<int?>("Workers:MaxRetryCount") ?? 3);
        var retryDelaySeconds = Math.Max(1, configuration.GetValue<int?>("Workers:RetryDelaySeconds") ?? 5);
        var retryBefore = DateTime.UtcNow.AddSeconds(-retryDelaySeconds);

        var messages = await dbContext.OutboxMessages
            .Where(x =>
                x.Status == OutboxMessageStatus.Pending ||
                (x.Status == OutboxMessageStatus.Failed &&
                 x.RetryCount < maxRetries &&
                 (x.ProcessedAtUtc == null || x.ProcessedAtUtc <= retryBefore)))
            .OrderBy(x => x.CreatedAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (messages.Count == 0)
            return OutboxProcessingResult.Empty;

        var ok = 0;
        var failed = 0;

        foreach (var message in messages)
        {
            try
            {
                message.Status = OutboxMessageStatus.Processing;
                await dbContext.SaveChangesAsync(cancellationToken);

                var payload = JsonSerializer.Deserialize<object>(message.PayloadJson);
                var stream =
                    configuration[$"Redis:Streams:Destinations:{message.EventName}"] ??
                    configuration["Redis:Streams:DefaultStreamName"] ??
                    "dhole.agent.events";

                await publisher.PublishAsync(
                    new RedisStreamMessage
                    {
                        StreamName = stream,
                        MessageType = message.EventName,
                        Payload = payload ?? message.PayloadJson,
                        Headers = Headers(message)
                    },
                    cancellationToken);

                message.Status = OutboxMessageStatus.Processed;
                message.ProcessedAtUtc = DateTime.UtcNow;
                message.ErrorMessage = null;
                ok++;
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.Status = OutboxMessageStatus.Failed;
                message.ProcessedAtUtc = DateTime.UtcNow;
                message.ErrorMessage = ex.Message;
                failed++;

                logger.LogError(
                    ex,
                    "Failed to publish agent outbox event {EventId}. Retry {RetryCount}/{MaxRetries}.",
                    message.EventId,
                    message.RetryCount,
                    maxRetries);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var more = await dbContext.OutboxMessages.AnyAsync(
            x => x.Status == OutboxMessageStatus.Pending ||
                 (x.Status == OutboxMessageStatus.Failed &&
                  x.RetryCount < maxRetries &&
                  (x.ProcessedAtUtc == null || x.ProcessedAtUtc <= retryBefore)),
            cancellationToken);

        return new OutboxProcessingResult(ok, failed, more);
    }

    private static Dictionary<string, string> Headers(OutboxMessage message)
    {
        var headers = new Dictionary<string, string>
        {
            ["event_id"] = message.EventId.ToString(),
            ["event_type"] = message.EventType,
            ["event_name"] = message.EventName,
            ["source_service"] = message.SourceService,
            ["created_at_utc"] = message.CreatedAtUtc.ToString("O")
        };

        if (!string.IsNullOrWhiteSpace(message.CorrelationId))
            headers["correlation_id"] = message.CorrelationId;

        return headers;
    }
}
