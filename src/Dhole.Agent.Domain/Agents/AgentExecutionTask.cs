using CustomCodeFramework.Core.Domain.Entities;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentExecutionTask : AuditableAggregateRoot<Guid>
{
    private AgentExecutionTask() { }

    private AgentExecutionTask(Guid id, Guid executionId, Guid routeId, Guid equipmentId, string inputJson, int sortOrder) : base(id)
    {
        ExecutionId = executionId;
        RouteId = routeId;
        EquipmentId = equipmentId;
        Status = AgentExecutionTaskStatus.Pending;
        InputJson = string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson;
        SortOrder = Math.Max(0, sortOrder);
        MarkAsCreated(DateTime.UtcNow, null);
    }

    public Guid ExecutionId { get; private set; }
    public Guid RouteId { get; private set; }
    public Guid EquipmentId { get; private set; }
    public AgentExecutionTaskStatus Status { get; private set; }
    public string InputJson { get; private set; } = "{}";
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public int SortOrder { get; private set; }

    public static AgentExecutionTask Create(Guid executionId, Guid routeId, Guid equipmentId, string inputJson, int sortOrder)
        => new(Guid.NewGuid(), executionId, routeId, equipmentId, inputJson, sortOrder);

    public void Start(DateTime at)
    {
        if (Status != AgentExecutionTaskStatus.Pending)
            throw new InvalidOperationException($"Task cannot start from {Status}.");
        Status = AgentExecutionTaskStatus.Running;
        StartedAt = at;
        ErrorCode = null;
        ErrorMessage = null;
        MarkAsUpdated(DateTime.UtcNow, null);
    }

    public void Complete(DateTime at)
    {
        if (Status != AgentExecutionTaskStatus.Running)
            throw new InvalidOperationException($"Task cannot complete from {Status}.");
        Status = AgentExecutionTaskStatus.Completed;
        CompletedAt = at;
        MarkAsUpdated(DateTime.UtcNow, null);
    }

    public void Fail(string errorCode, string errorMessage, DateTime at)
    {
        Status = AgentExecutionTaskStatus.Failed;
        ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "task_failed" : errorCode.Trim();
        ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? "Task failed." : errorMessage.Trim();
        CompletedAt = at;
        MarkAsUpdated(DateTime.UtcNow, null);
    }

    public void Skip(string? reason, DateTime at)
    {
        Status = AgentExecutionTaskStatus.Skipped;
        ErrorMessage = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        CompletedAt = at;
        MarkAsUpdated(DateTime.UtcNow, null);
    }
}
