using CustomCodeFramework.Core.Domain.Entities;
using Dhole.Agent.Domain.Agents.Events;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentExecution : AuditableAggregateRoot<Guid>
{
    private AgentExecution() { }

    private AgentExecution(Guid id, Guid agentDefinitionId, Guid providerId, Guid? scheduleId, Guid? credentialId,
        AgentExecutionType executionType, int priority, string inputJson, int maxAttempts, string correlationId,
        string? traceId, Guid? createdBy) : base(id)
    {
        AgentDefinitionId = agentDefinitionId;
        ProviderId = providerId;
        ScheduleId = scheduleId;
        CredentialId = credentialId;
        ExecutionType = executionType;
        Status = AgentExecutionStatus.Pending;
        Priority = priority;
        InputJson = string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson;
        Attempt = 0;
        MaxAttempts = Math.Max(1, maxAttempts);
        CorrelationId = Required(correlationId);
        TraceId = Optional(traceId);
        MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
    }

    public Guid AgentDefinitionId { get; private set; }
    public Guid ProviderId { get; private set; }
    public Guid? ScheduleId { get; private set; }
    public Guid? CredentialId { get; private set; }
    public Guid? ExtractionProfileId { get; private set; }
    public string? PromptSnapshot { get; private set; }
    public string? ConfigurationSnapshotJson { get; private set; }
    public AgentExecutionType ExecutionType { get; private set; }
    public AgentExecutionStatus Status { get; private set; }
    public int Priority { get; private set; }
    public string InputJson { get; private set; } = "{}";
    public string? OutputJson { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public long? DurationMs { get; private set; }
    public int Attempt { get; private set; }
    public int MaxAttempts { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string? TraceId { get; private set; }

    public static AgentExecution Create(Guid agentDefinitionId, Guid providerId, Guid? scheduleId, Guid? credentialId,
        AgentExecutionType executionType, int priority, string inputJson, int maxAttempts, string correlationId,
        string? traceId = null, Guid? createdBy = null)
    {
        var entity = new AgentExecution(Guid.NewGuid(), agentDefinitionId, providerId, scheduleId, credentialId,
            executionType, priority, inputJson, maxAttempts, correlationId, traceId, createdBy);
        entity.AddDomainEvent(new AgentExecutionRequestedDomainEvent(entity.Id, providerId, agentDefinitionId, correlationId));
        return entity;
    }

    public void AttachProfileSnapshot(
        Guid extractionProfileId,
        string promptSnapshot,
        string configurationSnapshotJson,
        Guid? updatedBy = null)
    {
        if (Status is not (AgentExecutionStatus.Pending or AgentExecutionStatus.Queued))
            throw new InvalidOperationException("Profile snapshot can only be attached before execution starts.");

        ExtractionProfileId = extractionProfileId;
        PromptSnapshot = Required(promptSnapshot);
        ConfigurationSnapshotJson = string.IsNullOrWhiteSpace(configurationSnapshotJson) ? "{}" : configurationSnapshotJson;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void Queue(Guid? updatedBy = null)
    {
        EnsureState(AgentExecutionStatus.Pending);
        Status = AgentExecutionStatus.Queued;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void Start(DateTime startedAt, Guid? updatedBy = null)
    {
        if (Status is not (AgentExecutionStatus.Pending or AgentExecutionStatus.Queued or AgentExecutionStatus.WaitingForAuthentication))
            throw new InvalidOperationException($"Execution cannot start from {Status}.");
        Status = AgentExecutionStatus.Running;
        StartedAt = startedAt;
        Attempt++;
        ErrorCode = null;
        ErrorMessage = null;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
        AddDomainEvent(new AgentExecutionStartedDomainEvent(Id, ProviderId, startedAt, Attempt));
    }

    public void WaitForAuthentication(Guid? updatedBy = null)
    {
        if (Status != AgentExecutionStatus.Running) throw new InvalidOperationException("Execution is not running.");
        Status = AgentExecutionStatus.WaitingForAuthentication;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void Complete(string? outputJson, DateTime completedAt, bool partial = false, Guid? updatedBy = null)
    {
        if (Status != AgentExecutionStatus.Running) throw new InvalidOperationException("Execution is not running.");
        Status = partial ? AgentExecutionStatus.PartiallyCompleted : AgentExecutionStatus.Completed;
        OutputJson = Optional(outputJson);
        CompletedAt = completedAt;
        DurationMs = StartedAt.HasValue ? Math.Max(0, (long)(completedAt - StartedAt.Value).TotalMilliseconds) : null;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
        AddDomainEvent(new AgentExecutionCompletedDomainEvent(Id, ProviderId, completedAt, partial));
    }

    public void Fail(string errorCode, string errorMessage, DateTime failedAt, Guid? updatedBy = null)
    {
        if (Status is AgentExecutionStatus.Completed or AgentExecutionStatus.Cancelled)
            throw new InvalidOperationException($"Execution cannot fail from {Status}.");
        Status = AgentExecutionStatus.Failed;
        ErrorCode = Required(errorCode);
        ErrorMessage = Required(errorMessage);
        CompletedAt = failedAt;
        DurationMs = StartedAt.HasValue ? Math.Max(0, (long)(failedAt - StartedAt.Value).TotalMilliseconds) : null;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
        AddDomainEvent(new AgentExecutionFailedDomainEvent(Id, ProviderId, failedAt, ErrorCode));
    }

    public void Cancel(DateTime cancelledAt, Guid? updatedBy = null)
    {
        if (Status is AgentExecutionStatus.Completed or AgentExecutionStatus.Failed or AgentExecutionStatus.Cancelled)
            return;
        Status = AgentExecutionStatus.Cancelled;
        CompletedAt = cancelledAt;
        DurationMs = StartedAt.HasValue ? Math.Max(0, (long)(cancelledAt - StartedAt.Value).TotalMilliseconds) : null;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    private void EnsureState(AgentExecutionStatus state)
    {
        if (Status != state) throw new InvalidOperationException($"Expected {state}, actual {Status}.");
    }

    private static string Required(string value)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
