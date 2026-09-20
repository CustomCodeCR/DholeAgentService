using CustomCodeFramework.Core.Domain.Events;

namespace Dhole.Agent.Domain.Agents.Events;

public sealed record AgentProviderCreatedDomainEvent(Guid ProviderId, string Code, string Name, Guid? CreatedBy) : DomainEvent;
public sealed record AgentProviderUpdatedDomainEvent(Guid ProviderId, string Code, string Name, Guid? UpdatedBy) : DomainEvent;
public sealed record AgentProviderActivatedDomainEvent(Guid ProviderId, string Code, Guid? UpdatedBy) : DomainEvent;
public sealed record AgentProviderInactivatedDomainEvent(Guid ProviderId, string Code, Guid? UpdatedBy) : DomainEvent;

public sealed record AgentScheduleCreatedDomainEvent(Guid ScheduleId, string Name, Guid? CreatedBy) : DomainEvent;
public sealed record AgentScheduleUpdatedDomainEvent(Guid ScheduleId, string Name, Guid? UpdatedBy) : DomainEvent;
public sealed record AgentScheduleActivatedDomainEvent(Guid ScheduleId, string Name, Guid? UpdatedBy) : DomainEvent;
public sealed record AgentScheduleInactivatedDomainEvent(Guid ScheduleId, string Name, Guid? UpdatedBy) : DomainEvent;

public sealed record AgentExecutionRequestedDomainEvent(Guid ExecutionId, Guid ProviderId, Guid AgentDefinitionId, string CorrelationId) : DomainEvent;
public sealed record AgentExecutionStartedDomainEvent(Guid ExecutionId, Guid ProviderId, DateTime StartedAt, int Attempt) : DomainEvent;
public sealed record AgentExecutionCompletedDomainEvent(Guid ExecutionId, Guid ProviderId, DateTime CompletedAt, bool PartiallyCompleted) : DomainEvent;
public sealed record AgentExecutionFailedDomainEvent(Guid ExecutionId, Guid ProviderId, DateTime FailedAt, string ErrorCode) : DomainEvent;

public sealed record BrowserProfileAuthenticatedDomainEvent(Guid BrowserProfileId, Guid ProviderId, DateTime AuthenticatedAt) : DomainEvent;
public sealed record BrowserProfileExpiredDomainEvent(Guid BrowserProfileId, Guid ProviderId, DateTime ExpiredAt) : DomainEvent;
