using CustomCodeFramework.Core.Domain.Entities;
using Dhole.Agent.Domain.Agents.Events;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentSchedule : SoftDeletableAggregateRoot<Guid>
{
    private AgentSchedule() { }

    private AgentSchedule(Guid id, string name, Guid agentDefinitionId, Guid providerId, Guid? credentialId,
        Guid? extractionProfileId, AgentScheduleType scheduleType, string? cronExpression, int? intervalMinutes,
        DateTime? executeAt, string timezone, string inputJson, int maxRetries, int timeoutSeconds,
        Guid? createdBy) : base(id)
    {
        Name = Required(name);
        AgentDefinitionId = agentDefinitionId;
        ProviderId = providerId;
        CredentialId = credentialId;
        ExtractionProfileId = extractionProfileId;
        ApplySchedule(scheduleType, cronExpression, intervalMinutes, executeAt);
        Timezone = Required(timezone);
        InputJson = Required(inputJson);
        MaxRetries = Math.Max(0, maxRetries);
        TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        NextExecutionAt = scheduleType == AgentScheduleType.Once ? executeAt : null;
        IsActive = true;
        MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
    }

    public string Name { get; private set; } = string.Empty;
    public Guid AgentDefinitionId { get; private set; }
    public Guid ProviderId { get; private set; }
    public Guid? CredentialId { get; private set; }
    public Guid? ExtractionProfileId { get; private set; }
    public AgentScheduleType ScheduleType { get; private set; }
    public string? CronExpression { get; private set; }
    public int? IntervalMinutes { get; private set; }
    public DateTime? ExecuteAt { get; private set; }
    public string Timezone { get; private set; } = "UTC";
    public string InputJson { get; private set; } = "{}";
    public bool IsActive { get; private set; }
    public DateTime? LastExecutionAt { get; private set; }
    public DateTime? NextExecutionAt { get; private set; }
    public int MaxRetries { get; private set; }
    public int TimeoutSeconds { get; private set; }

    public static AgentSchedule Create(string name, Guid agentDefinitionId, Guid providerId, Guid? credentialId,
        Guid? extractionProfileId, AgentScheduleType scheduleType, string? cronExpression, int? intervalMinutes,
        DateTime? executeAt, string timezone, string inputJson, int maxRetries, int timeoutSeconds,
        Guid? createdBy = null)
    {
        var entity = new AgentSchedule(Guid.NewGuid(), name, agentDefinitionId, providerId, credentialId,
            extractionProfileId, scheduleType, cronExpression, intervalMinutes, executeAt, timezone, inputJson,
            maxRetries, timeoutSeconds, createdBy);
        entity.AddDomainEvent(new AgentScheduleCreatedDomainEvent(entity.Id, entity.Name, createdBy));
        return entity;
    }

    public void Update(string name, Guid? credentialId, Guid? extractionProfileId, AgentScheduleType scheduleType,
        string? cronExpression, int? intervalMinutes, DateTime? executeAt, string timezone, string inputJson,
        int maxRetries, int timeoutSeconds, DateTime? nextExecutionAt, Guid? updatedBy = null)
    {
        Name = Required(name);
        CredentialId = credentialId;
        ExtractionProfileId = extractionProfileId;
        ApplySchedule(scheduleType, cronExpression, intervalMinutes, executeAt);
        Timezone = Required(timezone);
        InputJson = Required(inputJson);
        MaxRetries = Math.Max(0, maxRetries);
        TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

        // Cron/Interval are recalculated by the dispatcher from their current configuration.
        // This avoids keeping a stale NextExecutionAt when the user edits cron/timezone.
        NextExecutionAt = scheduleType == AgentScheduleType.Once ? executeAt : null;

        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
        AddDomainEvent(new AgentScheduleUpdatedDomainEvent(Id, Name, updatedBy));
    }

    public void SetActive(bool isActive, Guid? updatedBy = null)
    {
        if (IsActive == isActive) return;
        IsActive = isActive;
        if (isActive && ScheduleType != AgentScheduleType.Once)
            NextExecutionAt = null;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
        AddDomainEvent(isActive
            ? new AgentScheduleActivatedDomainEvent(Id, Name, updatedBy)
            : new AgentScheduleInactivatedDomainEvent(Id, Name, updatedBy));
    }

    public void MarkDispatched(DateTime executedAt, DateTime? nextExecutionAt, Guid? updatedBy = null)
    {
        LastExecutionAt = executedAt;
        NextExecutionAt = nextExecutionAt;
        if (ScheduleType == AgentScheduleType.Once && nextExecutionAt is null) IsActive = false;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void SetNextExecution(DateTime? value, Guid? updatedBy = null)
    {
        NextExecutionAt = value;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    private void ApplySchedule(AgentScheduleType type, string? cron, int? interval, DateTime? executeAt)
    {
        if (type == AgentScheduleType.Cron && string.IsNullOrWhiteSpace(cron))
            throw new ArgumentException("Cron expression is required.", nameof(cron));
        if (type == AgentScheduleType.Interval && (!interval.HasValue || interval <= 0))
            throw new ArgumentOutOfRangeException(nameof(interval));
        if (type == AgentScheduleType.Once && !executeAt.HasValue)
            throw new ArgumentException("ExecuteAt is required for one-time schedules.", nameof(executeAt));
        ScheduleType = type;
        CronExpression = string.IsNullOrWhiteSpace(cron) ? null : cron.Trim();
        IntervalMinutes = interval;
        ExecuteAt = executeAt;
    }

    private static string Required(string value)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
}
