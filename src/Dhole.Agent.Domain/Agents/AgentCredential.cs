using CustomCodeFramework.Core.Domain.Entities;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentCredential : SoftDeletableAggregateRoot<Guid>
{
    private AgentCredential() { }

    private AgentCredential(
        Guid id,
        Guid providerId,
        string name,
        string usernameEncrypted,
        string passwordEncrypted,
        string? additionalSecretsEncrypted,
        Guid? createdBy) : base(id)
    {
        ProviderId = providerId;
        Name = Required(name);
        UsernameEncrypted = Required(usernameEncrypted);
        PasswordEncrypted = Required(passwordEncrypted);
        AdditionalSecretsEncrypted = Optional(additionalSecretsEncrypted);
        IsActive = true;
        MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
    }

    public Guid ProviderId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? UsernameEncrypted { get; private set; }
    public string? PasswordEncrypted { get; private set; }
    public string? AdditionalSecretsEncrypted { get; private set; }

    // Temporary compatibility for existing records that still reference external secrets.
    public string? UsernameSecretKey { get; private set; }
    public string? PasswordSecretKey { get; private set; }
    public string? AdditionalSecretsJson { get; private set; }

    public bool IsActive { get; private set; }
    public bool HasEncryptedSecrets =>
        !string.IsNullOrWhiteSpace(UsernameEncrypted) &&
        !string.IsNullOrWhiteSpace(PasswordEncrypted);

    public static AgentCredential CreateEncrypted(
        Guid providerId,
        string name,
        string usernameEncrypted,
        string passwordEncrypted,
        string? additionalSecretsEncrypted = null,
        Guid? createdBy = null)
        => new(Guid.NewGuid(), providerId, name, usernameEncrypted, passwordEncrypted, additionalSecretsEncrypted, createdBy);

    public static AgentCredential Create(
        Guid providerId,
        string name,
        string usernameSecretKey,
        string passwordSecretKey,
        string? additionalSecretsJson = null,
        Guid? createdBy = null)
    {
        var entity = new AgentCredential
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            Name = Required(name),
            UsernameSecretKey = Required(usernameSecretKey),
            PasswordSecretKey = Required(passwordSecretKey),
            AdditionalSecretsJson = Optional(additionalSecretsJson),
            IsActive = true
        };
        entity.MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
        return entity;
    }

    public void UpdateEncrypted(
        string name,
        string usernameEncrypted,
        string passwordEncrypted,
        string? additionalSecretsEncrypted,
        Guid? updatedBy = null)
    {
        Name = Required(name);
        UsernameEncrypted = Required(usernameEncrypted);
        PasswordEncrypted = Required(passwordEncrypted);
        AdditionalSecretsEncrypted = Optional(additionalSecretsEncrypted);
        UsernameSecretKey = null;
        PasswordSecretKey = null;
        AdditionalSecretsJson = null;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void Update(
        string name,
        string usernameSecretKey,
        string passwordSecretKey,
        string? additionalSecretsJson,
        Guid? updatedBy = null)
    {
        Name = Required(name);
        UsernameSecretKey = Required(usernameSecretKey);
        PasswordSecretKey = Required(passwordSecretKey);
        AdditionalSecretsJson = Optional(additionalSecretsJson);
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

    private static string? Optional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
