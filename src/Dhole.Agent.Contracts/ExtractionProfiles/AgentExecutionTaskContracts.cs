namespace Dhole.Agent.Contracts.ExtractionProfiles;

public sealed record AgentExecutionTaskDto(
    Guid Id,
    Guid ExecutionId,
    Guid RouteId,
    Guid EquipmentId,
    string Status,
    string InputJson,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ErrorCode,
    string? ErrorMessage,
    int SortOrder);
