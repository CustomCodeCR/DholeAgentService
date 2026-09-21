using CustomCodeFramework.Core.Domain.Entities;
using Dhole.Agent.Domain.Agents.Events;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentExtractionProfile : SoftDeletableAggregateRoot<Guid>
{
    private AgentExtractionProfile() { }

    private AgentExtractionProfile(
        Guid id,
        Guid providerId,
        Guid? credentialId,
        string name,
        string? description,
        string? baseUrl,
        string? loginUrl,
        string? searchUrl,
        string promptTemplate,
        AgentExecutionStrategy executionStrategy,
        string? parserKey,
        Guid? createdBy) : base(id)
    {
        ProviderId = providerId;
        CredentialId = credentialId;
        Name = Required(name);
        Description = Optional(description);
        BaseUrl = Optional(baseUrl);
        LoginUrl = Optional(loginUrl);
        SearchUrl = Optional(searchUrl);
        PromptTemplate = Required(promptTemplate);
        ExecutionStrategy = executionStrategy;
        ParserKey = Optional(parserKey);
        IsActive = true;
        MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
    }

    public Guid ProviderId { get; private set; }
    public Guid? CredentialId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? BaseUrl { get; private set; }
    public string? LoginUrl { get; private set; }
    public string? SearchUrl { get; private set; }
    public string PromptTemplate { get; private set; } = string.Empty;
    public AgentExecutionStrategy ExecutionStrategy { get; private set; }
    public string? ParserKey { get; private set; }
    public bool IsActive { get; private set; }

    public static AgentExtractionProfile Create(
        Guid providerId,
        Guid? credentialId,
        string name,
        string? description,
        string? baseUrl,
        string? loginUrl,
        string? searchUrl,
        string promptTemplate,
        AgentExecutionStrategy executionStrategy,
        string? parserKey,
        Guid? createdBy = null)
    {
        var entity = new AgentExtractionProfile(
            Guid.NewGuid(),
            providerId,
            credentialId,
            name,
            description,
            baseUrl,
            loginUrl,
            searchUrl,
            promptTemplate,
            executionStrategy,
            parserKey,
            createdBy);

        entity.AddDomainEvent(new AgentExtractionProfileCreatedDomainEvent(entity.Id, entity.ProviderId, entity.Name, createdBy));
        return entity;
    }

    public void Update(
        Guid? credentialId,
        string name,
        string? description,
        string? baseUrl,
        string? loginUrl,
        string? searchUrl,
        string promptTemplate,
        AgentExecutionStrategy executionStrategy,
        string? parserKey,
        Guid? updatedBy = null)
    {
        CredentialId = credentialId;
        Name = Required(name);
        Description = Optional(description);
        BaseUrl = Optional(baseUrl);
        LoginUrl = Optional(loginUrl);
        SearchUrl = Optional(searchUrl);
        PromptTemplate = Required(promptTemplate);
        ExecutionStrategy = executionStrategy;
        ParserKey = Optional(parserKey);
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
        AddDomainEvent(new AgentExtractionProfileUpdatedDomainEvent(Id, ProviderId, Name, updatedBy));
    }

    public void SetActive(bool isActive, Guid? updatedBy = null)
    {
        if (IsActive == isActive) return;

        IsActive = isActive;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
        AddDomainEvent(isActive
            ? new AgentExtractionProfileActivatedDomainEvent(Id, ProviderId, updatedBy)
            : new AgentExtractionProfileInactivatedDomainEvent(Id, ProviderId, updatedBy));
    }

    public void Delete(Guid? deletedBy = null)
    {
        if (IsDeleted) return;
        IsActive = false;
        MarkAsDeleted(DateTime.UtcNow, deletedBy?.ToString());
    }

    private static string Required(string value)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();

    private static string? Optional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
