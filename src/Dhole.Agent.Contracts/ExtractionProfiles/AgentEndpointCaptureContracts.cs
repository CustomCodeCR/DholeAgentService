namespace Dhole.Agent.Contracts.ExtractionProfiles;

public sealed record AgentEndpointCaptureDto(
    Guid Id,
    Guid ProfileId,
    string Name,
    string HttpMethod,
    string UrlPattern,
    string MatchType,
    string? ContentType,
    bool CaptureRequest,
    bool CaptureResponse,
    bool IsRequired,
    int TimeoutSeconds,
    bool IsActive,
    int SortOrder);

public sealed record SaveAgentEndpointCaptureRequest(
    string Name,
    string HttpMethod,
    string UrlPattern,
    string MatchType,
    string? ContentType,
    bool CaptureRequest,
    bool CaptureResponse,
    bool IsRequired,
    int TimeoutSeconds,
    bool IsActive,
    int SortOrder);

public sealed record TestAgentEndpointCaptureRequest(string HttpMethod, string Url, string? ContentType);
public sealed record TestAgentEndpointCaptureResponse(bool Matches, string RuleName, string MatchType, string UrlPattern);
