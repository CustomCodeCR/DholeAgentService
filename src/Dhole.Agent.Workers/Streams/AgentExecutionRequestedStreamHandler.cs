using System.Text.Json;
using CustomCodeFramework.Redis.Streams.Abstractions;
using CustomCodeFramework.Redis.Streams.Messages;
using Dhole.Agent.Application.Abstractions.Repositories;

namespace Dhole.Agent.Workers.Streams;

internal sealed class AgentExecutionRequestedStreamHandler(IAgentExecutionRepository executions,ILogger<AgentExecutionRequestedStreamHandler> logger):IRedisStreamMessageHandler
{
    public string MessageType=>"agent.execution.requested";

    public async Task HandleAsync(RedisStreamEnvelope envelope,CancellationToken cancellationToken=default)
    {
        using var doc=JsonDocument.Parse(envelope.PayloadJson);
        if(!TryGetExecutionId(doc.RootElement,out var executionId))
        {
            logger.LogWarning("Agent execution request {MessageId} has no execution id.",envelope.MessageId);
            return;
        }
        var execution=await executions.GetByIdAsync(executionId,cancellationToken);
        if(execution is null)
        {
            logger.LogWarning("Agent execution {ExecutionId} was not found.",executionId);
            return;
        }
        logger.LogInformation("Agent execution {ExecutionId} is queued for runtime processing.",executionId);
    }

    private static bool TryGetExecutionId(JsonElement root,out Guid id)
    {
        id=Guid.Empty;
        foreach(var name in new[]{"ExecutionId","executionId","Id","id"})
            if(root.TryGetProperty(name,out var p)&&Guid.TryParse(p.GetString(),out id))return true;
        return false;
    }
}
