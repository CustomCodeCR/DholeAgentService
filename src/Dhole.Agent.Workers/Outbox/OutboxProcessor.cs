using System.Text.Json;
using CustomCodeFramework.Messaging.Outbox;
using CustomCodeFramework.Messaging.Outbox.Processing;
using CustomCodeFramework.Redis.Streams.Abstractions;
using CustomCodeFramework.Redis.Streams.Messages;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.Workers.Outbox;

internal sealed class OutboxProcessor(ServiceDbContext dbContext,IRedisStreamPublisher publisher,IConfiguration configuration,ILogger<OutboxProcessor> logger):IOutboxProcessor
{
    public async Task<OutboxProcessingResult> ProcessAsync(int batchSize,CancellationToken cancellationToken=default)
    {
        var messages=await dbContext.OutboxMessages.Where(x=>x.Status==OutboxMessageStatus.Pending).OrderBy(x=>x.CreatedAtUtc).Take(batchSize).ToListAsync(cancellationToken);
        if(messages.Count==0)return OutboxProcessingResult.Empty;
        var ok=0;var failed=0;
        foreach(var message in messages)
        {
            try
            {
                var payload=JsonSerializer.Deserialize<object>(message.PayloadJson);
                var stream=configuration[$"Redis:Streams:Destinations:{message.EventName}"]??configuration["Redis:Streams:DefaultStreamName"]??"dhole.agent.events";
                await publisher.PublishAsync(new RedisStreamMessage{StreamName=stream,MessageType=message.EventName,Payload=payload??message.PayloadJson,Headers=Headers(message)},cancellationToken);
                message.Status=OutboxMessageStatus.Processed;message.ProcessedAtUtc=DateTime.UtcNow;message.ErrorMessage=null;ok++;
            }
            catch(Exception ex)
            {
                message.RetryCount++;message.Status=OutboxMessageStatus.Failed;message.ErrorMessage=ex.Message;failed++;
                logger.LogError(ex,"Failed to publish agent outbox event {EventId}",message.EventId);
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        var more=await dbContext.OutboxMessages.AnyAsync(x=>x.Status==OutboxMessageStatus.Pending,cancellationToken);
        return new OutboxProcessingResult(ok,failed,more);
    }
    private static Dictionary<string,string> Headers(OutboxMessage m)
    {
        var h=new Dictionary<string,string>{{"event_id",m.EventId.ToString()},{"event_type",m.EventType},{"event_name",m.EventName},{"source_service",m.SourceService},{"created_at_utc",m.CreatedAtUtc.ToString("O")}};
        if(!string.IsNullOrWhiteSpace(m.CorrelationId))h["correlation_id"]=m.CorrelationId;
        return h;
    }
}
