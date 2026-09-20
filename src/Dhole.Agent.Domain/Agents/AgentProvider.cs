using CustomCodeFramework.Core.Domain.Entities;
using Dhole.Agent.Domain.Agents.Events;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentProvider : SoftDeletableAggregateRoot<Guid>
{
    private AgentProvider() { }

    private AgentProvider(Guid id, string code, string name, AgentProviderType providerType, string? baseUrl,
        AgentExecutionStrategy defaultExecutionStrategy, bool isSystem, string? metadataJson, Guid? createdBy) : base(id)
    {
        Code = NormalizeRequired(code, nameof(code)).ToUpperInvariant();
        Name = NormalizeRequired(name, nameof(name));
        ProviderType = providerType;
        BaseUrl = NormalizeOptional(baseUrl);
        DefaultExecutionStrategy = defaultExecutionStrategy;
        IsSystem = isSystem;
        IsActive = true;
        MetadataJson = NormalizeOptional(metadataJson);
        MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
    }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public AgentProviderType ProviderType { get; private set; }
    public string? BaseUrl { get; private set; }
    public AgentExecutionStrategy DefaultExecutionStrategy { get; private set; }
    public bool IsSystem { get; private set; }
    public bool IsActive { get; private set; }
    public string? MetadataJson { get; private set; }

    public static AgentProvider Create(string code, string name, AgentProviderType providerType, string? baseUrl,
        AgentExecutionStrategy defaultExecutionStrategy, bool isSystem, string? metadataJson, Guid? createdBy = null)
    {
        var entity = new AgentProvider(Guid.NewGuid(), code, name, providerType, baseUrl, defaultExecutionStrategy,
            isSystem, metadataJson, createdBy);
        entity.AddDomainEvent(new AgentProviderCreatedDomainEvent(entity.Id, entity.Code, entity.Name, createdBy));
        return entity;
    }

    public void Update(string name, AgentProviderType providerType, string? baseUrl,
        AgentExecutionStrategy defaultExecutionStrategy, string? metadataJson, Guid? updatedBy = null)
    {
        Name = NormalizeRequired(name, nameof(name));
        ProviderType = providerType;
        BaseUrl = NormalizeOptional(baseUrl);
        DefaultExecutionStrategy = defaultExecutionStrategy;
        MetadataJson = NormalizeOptional(metadataJson);
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
        AddDomainEvent(new AgentProviderUpdatedDomainEvent(Id, Code, Name, updatedBy));
    }

    public void SetActive(bool isActive, Guid? updatedBy = null)
    {
        if (IsActive == isActive) return;
        IsActive = isActive;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
        AddDomainEvent(isActive
            ? new AgentProviderActivatedDomainEvent(Id, Code, updatedBy)
            : new AgentProviderInactivatedDomainEvent(Id, Code, updatedBy));
    }

    public void Delete(Guid? deletedBy = null)
    {
        if (IsSystem) throw new InvalidOperationException("System providers cannot be deleted.");
        MarkAsDeleted(DateTime.UtcNow, deletedBy?.ToString());
    }

    private static string NormalizeRequired(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", parameter);
        return value.Trim();
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
