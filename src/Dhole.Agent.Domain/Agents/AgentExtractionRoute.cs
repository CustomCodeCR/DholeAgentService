using CustomCodeFramework.Core.Domain.Entities;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentExtractionRoute : SoftDeletableAggregateRoot<Guid>
{
    private AgentExtractionRoute() { }

    private AgentExtractionRoute(
        Guid id,
        Guid profileId,
        string? name,
        string? polCode,
        string polName,
        string? poeCode,
        string? poeName,
        string? podCode,
        string podName,
        bool isActive,
        int sortOrder,
        Guid? createdBy) : base(id)
    {
        ProfileId = profileId;
        Apply(name, polCode, polName, poeCode, poeName, podCode, podName, isActive, sortOrder);
        MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
    }

    public Guid ProfileId { get; private set; }
    public string? Name { get; private set; }
    public string? PolCode { get; private set; }
    public string PolName { get; private set; } = string.Empty;
    public string? PoeCode { get; private set; }
    public string? PoeName { get; private set; }
    public string? PodCode { get; private set; }
    public string PodName { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public int SortOrder { get; private set; }

    public static AgentExtractionRoute Create(
        Guid profileId,
        string? name,
        string? polCode,
        string polName,
        string? poeCode,
        string? poeName,
        string? podCode,
        string podName,
        bool isActive = true,
        int sortOrder = 0,
        Guid? createdBy = null)
        => new(Guid.NewGuid(), profileId, name, polCode, polName, poeCode, poeName, podCode, podName, isActive, sortOrder, createdBy);

    public void Update(
        string? name,
        string? polCode,
        string polName,
        string? poeCode,
        string? poeName,
        string? podCode,
        string podName,
        bool isActive,
        int sortOrder,
        Guid? updatedBy = null)
    {
        Apply(name, polCode, polName, poeCode, poeName, podCode, podName, isActive, sortOrder);
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void Delete(Guid? deletedBy = null)
    {
        if (IsDeleted) return;
        IsActive = false;
        MarkAsDeleted(DateTime.UtcNow, deletedBy?.ToString());
    }

    private void Apply(string? name, string? polCode, string polName, string? poeCode, string? poeName, string? podCode, string podName, bool isActive, int sortOrder)
    {
        Name = Optional(name);
        PolCode = Optional(polCode);
        PolName = Required(polName);
        PoeCode = Optional(poeCode);
        PoeName = Optional(poeName);
        PodCode = Optional(podCode);
        PodName = Required(podName);
        IsActive = isActive;
        SortOrder = Math.Max(0, sortOrder);
    }

    private static string Required(string value)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
    private static string? Optional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
