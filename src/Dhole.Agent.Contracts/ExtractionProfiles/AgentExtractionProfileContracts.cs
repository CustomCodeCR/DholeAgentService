namespace Dhole.Agent.Contracts.ExtractionProfiles;

public sealed record AgentExtractionProfileDto(
    Guid Id,
    Guid ProviderId,
    Guid? CredentialId,
    string Name,
    string? Description,
    string? BaseUrl,
    string? LoginUrl,
    string? SearchUrl,
    string PromptTemplate,
    string ExecutionStrategy,
    string? ParserKey,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public sealed record CreateAgentExtractionProfileRequest(
    Guid ProviderId,
    Guid? CredentialId,
    string Name,
    string? Description,
    string? BaseUrl,
    string? LoginUrl,
    string? SearchUrl,
    string PromptTemplate,
    string ExecutionStrategy,
    string? ParserKey);

public sealed record UpdateAgentExtractionProfileRequest(
    Guid? CredentialId,
    string Name,
    string? Description,
    string? BaseUrl,
    string? LoginUrl,
    string? SearchUrl,
    string PromptTemplate,
    string ExecutionStrategy,
    string? ParserKey);
