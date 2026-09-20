using CustomCodeFramework.Core.Domain.Entities;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentCredential : SoftDeletableAggregateRoot<Guid>
{
    private AgentCredential() { }

    private AgentCredential(Guid id, Guid providerId, string name, string usernameSecretKey, string passwordSecretKey,
        string? additionalSecretsJson, Guid? createdBy) : base(id)
    {
        ProviderId = providerId;
        Name = Required(name);
        UsernameSecretKey = Required(usernameSecretKey);
        PasswordSecretKey = Required(passwordSecretKey);
        AdditionalSecretsJson = string.IsNullOrWhiteSpace(additionalSecretsJson) ? null : additionalSecretsJson;
        IsActive = true;
        MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
    }

    public Guid ProviderId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string UsernameSecretKey { get; private set; } = string.Empty;
    public string PasswordSecretKey { get; private set; } = string.Empty;
    public string? AdditionalSecretsJson { get; private set; }
    public bool IsActive { get; private set; }

    public static AgentCredential Create(Guid providerId, string name, string usernameSecretKey, string passwordSecretKey,
        string? additionalSecretsJson = null, Guid? createdBy = null)
        => new(Guid.NewGuid(), providerId, name, usernameSecretKey, passwordSecretKey, additionalSecretsJson, createdBy);

    public void Update(string name, string usernameSecretKey, string passwordSecretKey, string? additionalSecretsJson,
        Guid? updatedBy = null)
    {
        Name = Required(name);
        UsernameSecretKey = Required(usernameSecretKey);
        PasswordSecretKey = Required(passwordSecretKey);
        AdditionalSecretsJson = string.IsNullOrWhiteSpace(additionalSecretsJson) ? null : additionalSecretsJson;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void SetActive(bool value, Guid? updatedBy = null)
    {
        if (IsActive == value) return;
        IsActive = value;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    private static string Required(string value)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
}
