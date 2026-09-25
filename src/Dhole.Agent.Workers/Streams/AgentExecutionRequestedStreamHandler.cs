using System.Text.Json;
using CustomCodeFramework.Redis.Streams.Abstractions;
using CustomCodeFramework.Redis.Streams.Messages;

namespace Dhole.Agent.Workers.Streams;

/// <summary>
/// Execution requests are acknowledged from Redis, but PostgreSQL is the
/// authoritative execution queue. QueuedExecutionBackgroundService performs the
/// actual execution so Redis delivery/locks cannot strand an execution.
/// </summary>
internal sealed class AgentExecutionRequestedStreamHandler(
    ILogger<AgentExecutionRequestedStreamHandler> logger) : IRedisStreamMessageHandler
{
    public string MessageType => "agent.execution.requested";

    public Task HandleAsync(
        RedisStreamEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(envelope.PayloadJson);
        if (!TryGetExecutionId(doc.RootElement, out var executionId))
        {
            logger.LogWarning(
                "Agent execution request {MessageId} has no execution id.",
                envelope.MessageId);
            return Task.CompletedTask;
        }

        logger.LogDebug(
            "Acknowledging Redis request for execution {ExecutionId}; PostgreSQL queue pump owns execution dispatch.",
            executionId);

        return Task.CompletedTask;
    }

    private static bool TryGetExecutionId(JsonElement root, out Guid id)
    {
        id = Guid.Empty;
        foreach (var name in new[] { "ExecutionId", "executionId", "Id", "id" })
            if (root.TryGetProperty(name, out var property) &&
                Guid.TryParse(property.GetString(), out id))
                return true;

        return false;
    }
}
