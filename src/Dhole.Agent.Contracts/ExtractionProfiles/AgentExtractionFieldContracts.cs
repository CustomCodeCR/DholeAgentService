namespace Dhole.Agent.Contracts.ExtractionProfiles;

public sealed record AgentExtractionFieldDto(
    Guid Id,
    Guid ProfileId,
    string Key,
    string Label,
    string? Description,
    string DataType,
    string SourceType,
    string? JsonPath,
    bool Required,
    int SortOrder,
    bool IsActive);

public sealed record SaveAgentExtractionFieldRequest(
    string Key,
    string Label,
    string? Description,
    string DataType,
    string SourceType,
    string? JsonPath,
    bool Required,
    int SortOrder,
    bool IsActive);
