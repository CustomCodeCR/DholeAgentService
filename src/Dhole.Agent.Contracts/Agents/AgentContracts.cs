using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Contracts.Agents;

public sealed record AgentProviderDto(Guid Id,string Code,string Name,AgentProviderType ProviderType,string? BaseUrl,AgentExecutionStrategy DefaultExecutionStrategy,bool IsSystem,bool IsActive,string? MetadataJson,DateTime CreatedAtUtc,DateTime? UpdatedAtUtc);
public sealed record CreateAgentProviderRequest(string Code,string Name,AgentProviderType ProviderType,string? BaseUrl,AgentExecutionStrategy DefaultExecutionStrategy,bool IsSystem,string? MetadataJson);
public sealed record UpdateAgentProviderRequest(string Name,AgentProviderType ProviderType,string? BaseUrl,AgentExecutionStrategy DefaultExecutionStrategy,string? MetadataJson);

public sealed record AgentDefinitionDto(Guid Id,Guid ProviderId,string Code,string Name,string? Description,AgentActionType ActionType,AgentExecutionStrategy ExecutionStrategy,string? ConfigurationJson,bool IsActive,DateTime CreatedAtUtc,DateTime? UpdatedAtUtc);
public sealed record CreateAgentDefinitionRequest(Guid ProviderId,string Code,string Name,string? Description,AgentActionType ActionType,AgentExecutionStrategy ExecutionStrategy,string? ConfigurationJson);
public sealed record UpdateAgentDefinitionRequest(string Name,string? Description,AgentActionType ActionType,AgentExecutionStrategy ExecutionStrategy,string? ConfigurationJson);

public sealed record AgentCredentialDto(Guid Id,Guid ProviderId,string Name,bool IsActive,DateTime CreatedAtUtc,DateTime? UpdatedAtUtc);
public sealed record CreateAgentCredentialRequest(Guid ProviderId,string Name,string UsernameSecretKey,string PasswordSecretKey,string? AdditionalSecretsJson);
public sealed record UpdateAgentCredentialRequest(string Name,string UsernameSecretKey,string PasswordSecretKey,string? AdditionalSecretsJson);

public sealed record BrowserProfileDto(Guid Id,Guid ProviderId,Guid CredentialId,string Name,string ProfileKey,string StoragePath,BrowserProfileStatus Status,DateTime? LastLoginAt,DateTime? LastUsedAt,DateTime? SessionExpiresAt,bool IsActive);
public sealed record CreateBrowserProfileRequest(Guid ProviderId,Guid CredentialId,string Name,string ProfileKey,string StoragePath);

public sealed record AgentScheduleDto(Guid Id,string Name,Guid AgentDefinitionId,Guid ProviderId,Guid? CredentialId,AgentScheduleType ScheduleType,string? CronExpression,int? IntervalMinutes,DateTime? ExecuteAt,string Timezone,string InputJson,bool IsActive,DateTime? LastExecutionAt,DateTime? NextExecutionAt,int MaxRetries,int TimeoutSeconds);
public sealed record CreateAgentScheduleRequest(string Name,Guid AgentDefinitionId,Guid ProviderId,Guid? CredentialId,AgentScheduleType ScheduleType,string? CronExpression,int? IntervalMinutes,DateTime? ExecuteAt,string Timezone,string InputJson,int MaxRetries,int TimeoutSeconds);
public sealed record UpdateAgentScheduleRequest(string Name,Guid? CredentialId,AgentScheduleType ScheduleType,string? CronExpression,int? IntervalMinutes,DateTime? ExecuteAt,string Timezone,string InputJson,int MaxRetries,int TimeoutSeconds,DateTime? NextExecutionAt);

public sealed record AgentExecutionDto(Guid Id,Guid AgentDefinitionId,Guid ProviderId,Guid? ScheduleId,Guid? CredentialId,AgentExecutionType ExecutionType,AgentExecutionStatus Status,int Priority,string InputJson,string? OutputJson,DateTime? StartedAt,DateTime? CompletedAt,long? DurationMs,int Attempt,int MaxAttempts,string? ErrorCode,string? ErrorMessage,string CorrelationId,string? TraceId,DateTime CreatedAtUtc);
public sealed record CreateAgentExecutionRequest(Guid AgentDefinitionId,Guid ProviderId,Guid? CredentialId,int Priority,string InputJson,int MaxAttempts,string? CorrelationId,string? TraceId);
