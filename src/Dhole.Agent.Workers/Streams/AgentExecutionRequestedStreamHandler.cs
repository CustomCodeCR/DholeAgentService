using System.Text.Json;
using CustomCodeFramework.Redis.Abstractions;
using CustomCodeFramework.Redis.Streams.Abstractions;
using CustomCodeFramework.Redis.Streams.Messages;
using Dhole.Agent.Application.Abstractions.Runtime;

namespace Dhole.Agent.Workers.Streams;

internal sealed class AgentExecutionRequestedStreamHandler(
    IAgentExecutionOrchestrator orchestrator,
    IDistributedLock distributedLock,
    ILogger<AgentExecutionRequestedStreamHandler> logger) : IRedisStreamMessageHandler
{
    public string MessageType => "agent.execution.requested";

    public async Task HandleAsync(RedisStreamEnvelope envelope, CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(envelope.PayloadJson);
        if (!TryGetExecutionId(doc.RootElement, out var executionId))
        {
            logger.LogWarning("Agent execution request {MessageId} has no execution id.", envelope.MessageId);
            return;
        }

        await using var handle = await distributedLock.AcquireAsync(
            $"agent:execution:{executionId:N}",
            TimeSpan.FromMinutes(10),
            cancellationToken);

        if (handle is null)
        {
            logger.LogInformation(
                "Execution {ExecutionId} is already being processed by another worker.",
                executionId);
            return;
        }

        await orchestrator.ExecuteAsync(executionId, cancellationToken);
    }

    private static bool TryGetExecutionId(JsonElement root, out Guid id)
    {
        id = Guid.Empty;
        foreach (var name in new[] { "ExecutionId", "executionId", "Id", "id" })
            if (root.TryGetProperty(name, out var p) && Guid.TryParse(p.GetString(), out id))
                return true;
        return false;
    }
}
