using CustomCodeFramework.Core.Domain.Entities;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentExtractionEquipment : SoftDeletableAggregateRoot<Guid>
{
    private AgentExtractionEquipment() { }

    private AgentExtractionEquipment(Guid id, Guid profileId, string code, string name, int quantity, decimal defaultWeightKg, bool isActive, int sortOrder, Guid? createdBy) : base(id)
    {
        ProfileId = profileId;
        Apply(code, name, quantity, defaultWeightKg, isActive, sortOrder);
        MarkAsCreated(DateTime.UtcNow, createdBy?.ToString());
    }

    public Guid ProfileId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public decimal DefaultWeightKg { get; private set; }
    public bool IsActive { get; private set; }
    public int SortOrder { get; private set; }

    public static AgentExtractionEquipment Create(Guid profileId, string code, string name, int quantity, decimal defaultWeightKg, bool isActive = true, int sortOrder = 0, Guid? createdBy = null)
        => new(Guid.NewGuid(), profileId, code, name, quantity, defaultWeightKg, isActive, sortOrder, createdBy);

    public void Update(string code, string name, int quantity, decimal defaultWeightKg, bool isActive, int sortOrder, Guid? updatedBy = null)
    {
        Apply(code, name, quantity, defaultWeightKg, isActive, sortOrder);
        MarkAsUpdated(DateTime.UtcNow, updatedBy?.ToString());
    }

    public void Delete(Guid? deletedBy = null)
    {
        if (IsDeleted) return;
        IsActive = false;
        MarkAsDeleted(DateTime.UtcNow, deletedBy?.ToString());
    }

    private void Apply(string code, string name, int quantity, decimal defaultWeightKg, bool isActive, int sortOrder)
    {
        Code = Required(code).ToUpperInvariant();
        Name = Required(name);
        Quantity = quantity > 0 ? quantity : throw new ArgumentOutOfRangeException(nameof(quantity));
        DefaultWeightKg = defaultWeightKg >= 0 ? defaultWeightKg : throw new ArgumentOutOfRangeException(nameof(defaultWeightKg));
        IsActive = isActive;
        SortOrder = Math.Max(0, sortOrder);
    }

    private static string Required(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
}
