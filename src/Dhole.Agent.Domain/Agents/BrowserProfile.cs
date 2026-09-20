using CustomCodeFramework.Core.Domain.Entities;
using Dhole.Agent.Domain.Agents.Events;

namespace Dhole.Agent.Domain.Agents;

public sealed class BrowserProfile : SoftDeletableAggregateRoot<Guid>
{
    private BrowserProfile() { }

    private BrowserProfile(Guid id, Guid providerId, Guid credentialId, string name, string profileKey,
        string storagePath, Guid? createdBy) : base(id)
    {
        ProviderId = providerId;
        CredentialId = credentialId;
        Name = Required(name);
        ProfileKey = Required(profileKey);
        StoragePath = Required(storagePath);
        Status = BrowserProfileStatus.Unknown;
        IsActive = true;
        MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
    }

    public Guid ProviderId { get; private set; }
    public Guid CredentialId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string ProfileKey { get; private set; } = string.Empty;
    public string StoragePath { get; private set; } = string.Empty;
    public BrowserProfileStatus Status { get; private set; }
    public DateTime? LastLoginAt { get; private set; }
    public DateTime? LastUsedAt { get; private set; }
    public DateTime? SessionExpiresAt { get; private set; }
    public bool IsActive { get; private set; }

    public static BrowserProfile Create(Guid providerId, Guid credentialId, string name, string profileKey,
        string storagePath, Guid? createdBy = null)
        => new(Guid.NewGuid(), providerId, credentialId, name, profileKey, storagePath, createdBy);

    public void SetStatus(BrowserProfileStatus status, Guid? updatedBy = null)
    {
        Status = status;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void Authenticate(DateTime authenticatedAt, DateTime? expiresAt = null, Guid? updatedBy = null)
    {
        Status = BrowserProfileStatus.Authenticated;
        LastLoginAt = authenticatedAt;
        LastUsedAt = authenticatedAt;
        SessionExpiresAt = expiresAt;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
        AddDomainEvent(new BrowserProfileAuthenticatedDomainEvent(Id, ProviderId, authenticatedAt));
    }

    public void MarkUsed(DateTime usedAt, Guid? updatedBy = null)
    {
        LastUsedAt = usedAt;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void Expire(DateTime expiredAt, Guid? updatedBy = null)
    {
        Status = BrowserProfileStatus.Expired;
        SessionExpiresAt = expiredAt;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
        AddDomainEvent(new BrowserProfileExpiredDomainEvent(Id, ProviderId, expiredAt));
    }

    public void SetActive(bool value, Guid? updatedBy = null)
    {
        IsActive = value;
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    private static string Required(string value)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
}
