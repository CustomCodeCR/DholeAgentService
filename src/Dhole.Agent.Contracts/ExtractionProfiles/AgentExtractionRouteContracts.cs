namespace Dhole.Agent.Contracts.ExtractionProfiles;

public sealed record AgentExtractionRouteDto(
    Guid Id,
    Guid ProfileId,
    string? Name,
    string? PolCode,
    string PolName,
    string? PoeCode,
    string? PoeName,
    string? PodCode,
    string PodName,
    bool IsActive,
    int SortOrder);

public sealed record SaveAgentExtractionRouteRequest(
    string? Name,
    string? PolCode,
    string PolName,
    string? PoeCode,
    string? PoeName,
    string? PodCode,
    string PodName,
    bool IsActive,
    int SortOrder);
