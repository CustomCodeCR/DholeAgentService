using CustomCodeFramework.Core.Domain.Entities;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentDefinition : SoftDeletableAggregateRoot<Guid>
{
    private AgentDefinition() { }

    private AgentDefinition(Guid id, Guid providerId, string code, string name, string? description,
        AgentActionType actionType, AgentExecutionStrategy executionStrategy, string? configurationJson, Guid? createdBy) : base(id)
    {
        ProviderId = providerId;
        Code = Required(code).ToUpperInvariant();
        Name = Required(name);
        Description = Optional(description);
        ActionType = actionType;
        ExecutionStrategy = executionStrategy;
        ConfigurationJson = Optional(configurationJson);
        IsActive = true;
        MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
    }

    public Guid ProviderId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public AgentActionType ActionType { get; private set; }
    public AgentExecutionStrategy ExecutionStrategy { get; private set; }
    public string? ConfigurationJson { get; private set; }
    public bool IsActive { get; private set; }

    public static AgentDefinition Create(Guid providerId, string code, string name, string? description,
        AgentActionType actionType, AgentExecutionStrategy executionStrategy, string? configurationJson, Guid? createdBy = null)
        => new(Guid.NewGuid(), providerId, code, name, description, actionType, executionStrategy, configurationJson, createdBy);

    public void Update(string name, string? description, AgentActionType actionType,
        AgentExecutionStrategy executionStrategy, string? configurationJson, Guid? updatedBy = null)
    {
        Name = Required(name);
        Description = Optional(description);
        ActionType = actionType;
        ExecutionStrategy = executionStrategy;
        ConfigurationJson = Optional(configurationJson);
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void SetActive(bool isActive, Guid? updatedBy = null)
    {
        if (IsActive == isActive) return;
        IsActive = isActive;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    private static string Required(string value)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
