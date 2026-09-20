namespace Dhole.Agent.Contracts.Agents;

public sealed record AgentResultDto(
    Guid Id,
    Guid ExecutionId,
    Guid ProviderId,
    string ResultType,
    string SchemaVersion,
    string DataJson,
    DateTime CreatedAtUtc);
