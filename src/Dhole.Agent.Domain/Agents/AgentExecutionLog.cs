using CustomCodeFramework.Core.Domain.Entities;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentExecutionLog : AuditableAggregateRoot<Guid>
{
    private AgentExecutionLog() { }

    private AgentExecutionLog(Guid id, Guid executionId, string level, string category, string message,
        string? payloadJson, DateTime occurredAt) : base(id)
    {
        ExecutionId = executionId;
        Level = Required(level);
        Category = Required(category);
        Message = Required(message);
        PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? null : payloadJson;
        OccurredAt = occurredAt;
        MarkAsCreated(occurredAt, null);
    }

    public Guid ExecutionId { get; private set; }
    public string Level { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;
    public string? PayloadJson { get; private set; }
    public DateTime OccurredAt { get; private set; }

    public static AgentExecutionLog Create(Guid executionId, string level, string category, string message,
        string? payloadJson = null, DateTime? occurredAt = null)
        => new(Guid.NewGuid(), executionId, level, category, message, payloadJson, occurredAt ?? DateTime.UtcNow);

    private static string Required(string value)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
}
