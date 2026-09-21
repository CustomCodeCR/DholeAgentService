using CustomCodeFramework.Core.Domain.Entities;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentEndpointCapture : SoftDeletableAggregateRoot<Guid>
{
    private AgentEndpointCapture() { }

    private AgentEndpointCapture(
        Guid id,
        Guid profileId,
        string name,
        string httpMethod,
        string urlPattern,
        AgentEndpointMatchType matchType,
        string? contentType,
        bool captureRequest,
        bool captureResponse,
        bool isRequired,
        int timeoutSeconds,
        bool isActive,
        int sortOrder,
        Guid? createdBy) : base(id)
    {
        ProfileId = profileId;
        Apply(name, httpMethod, urlPattern, matchType, contentType, captureRequest, captureResponse, isRequired, timeoutSeconds, isActive, sortOrder);
        MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
    }

    public Guid ProfileId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string HttpMethod { get; private set; } = string.Empty;
    public string UrlPattern { get; private set; } = string.Empty;
    public AgentEndpointMatchType MatchType { get; private set; }
    public string? ContentType { get; private set; }
    public bool CaptureRequest { get; private set; }
    public bool CaptureResponse { get; private set; }
    public bool IsRequired { get; private set; }
    public int TimeoutSeconds { get; private set; }
    public bool IsActive { get; private set; }
    public int SortOrder { get; private set; }

    public static AgentEndpointCapture Create(
        Guid profileId,
        string name,
        string httpMethod,
        string urlPattern,
        AgentEndpointMatchType matchType,
        string? contentType,
        bool captureRequest,
        bool captureResponse,
        bool isRequired,
        int timeoutSeconds,
        bool isActive = true,
        int sortOrder = 0,
        Guid? createdBy = null)
        => new(Guid.NewGuid(), profileId, name, httpMethod, urlPattern, matchType, contentType, captureRequest, captureResponse, isRequired, timeoutSeconds, isActive, sortOrder, createdBy);

    public void Update(
        string name,
        string httpMethod,
        string urlPattern,
        AgentEndpointMatchType matchType,
        string? contentType,
        bool captureRequest,
        bool captureResponse,
        bool isRequired,
        int timeoutSeconds,
        bool isActive,
        int sortOrder,
        Guid? updatedBy = null)
    {
        Apply(name, httpMethod, urlPattern, matchType, contentType, captureRequest, captureResponse, isRequired, timeoutSeconds, isActive, sortOrder);
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void Delete(Guid? deletedBy = null)
    {
        if (IsDeleted) return;
        IsActive = false;
        MarkAsDeleted(DateTime.UtcNow, deletedBy?.ToString());
    }

    private void Apply(string name, string httpMethod, string urlPattern, AgentEndpointMatchType matchType, string? contentType, bool captureRequest, bool captureResponse, bool isRequired, int timeoutSeconds, bool isActive, int sortOrder)
    {
        Name = Required(name);
        HttpMethod = Required(httpMethod).ToUpperInvariant();
        UrlPattern = Required(urlPattern);
        MatchType = matchType;
        ContentType = Optional(contentType);
        CaptureRequest = captureRequest;
        CaptureResponse = captureResponse;
        IsRequired = isRequired;
        TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        IsActive = isActive;
        SortOrder = Math.Max(0, sortOrder);
    }

    private static string Required(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
