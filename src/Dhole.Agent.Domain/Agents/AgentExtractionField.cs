using CustomCodeFramework.Core.Domain.Entities;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentExtractionField : SoftDeletableAggregateRoot<Guid>
{
    private AgentExtractionField() { }

    private AgentExtractionField(
        Guid id,
        Guid profileId,
        string key,
        string label,
        string? description,
        AgentExtractionDataType dataType,
        AgentExtractionSourceType sourceType,
        string? jsonPath,
        bool required,
        int sortOrder,
        bool isActive,
        Guid? createdBy) : base(id)
    {
        ProfileId = profileId;
        Apply(key, label, description, dataType, sourceType, jsonPath, required, sortOrder, isActive);
        MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
    }

    public Guid ProfileId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Label { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public AgentExtractionDataType DataType { get; private set; }
    public AgentExtractionSourceType SourceType { get; private set; }
    public string? JsonPath { get; private set; }
    public bool Required { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; }

    public static AgentExtractionField Create(Guid profileId, string key, string label, string? description,
        AgentExtractionDataType dataType, AgentExtractionSourceType sourceType, string? jsonPath, bool required,
        int sortOrder = 0, bool isActive = true, Guid? createdBy = null)
        => new(Guid.NewGuid(), profileId, key, label, description, dataType, sourceType, jsonPath, required, sortOrder, isActive, createdBy);

    public void Update(string key, string label, string? description, AgentExtractionDataType dataType,
        AgentExtractionSourceType sourceType, string? jsonPath, bool required, int sortOrder, bool isActive,
        Guid? updatedBy = null)
    {
        Apply(key, label, description, dataType, sourceType, jsonPath, required, sortOrder, isActive);
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void Delete(Guid? deletedBy = null)
    {
        if (IsDeleted) return;
        IsActive = false;
        MarkAsDeleted(DateTime.UtcNow, deletedBy?.ToString());
    }

    private void Apply(string key, string label, string? description, AgentExtractionDataType dataType,
        AgentExtractionSourceType sourceType, string? jsonPath, bool required, int sortOrder, bool isActive)
    {
        Key = RequiredText(key);
        Label = RequiredText(label);
        Description = Optional(description);
        DataType = dataType;
        SourceType = sourceType;
        JsonPath = Optional(jsonPath);
        if (sourceType == AgentExtractionSourceType.JsonPath && string.IsNullOrWhiteSpace(JsonPath))
            throw new ArgumentException("JsonPath is required when source type is JsonPath.", nameof(jsonPath));
        Required = required;
        SortOrder = Math.Max(0, sortOrder);
        IsActive = isActive;
    }

    private static string RequiredText(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
