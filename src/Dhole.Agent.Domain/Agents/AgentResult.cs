using CustomCodeFramework.Core.Domain.Entities;

namespace Dhole.Agent.Domain.Agents;

public sealed class AgentResult : AuditableAggregateRoot<Guid>
{
    public const string OceanFreightRates = "OceanFreightRates";

    private AgentResult() { }

    private AgentResult(Guid id, Guid executionId, Guid providerId, string resultType, string schemaVersion,
        string dataJson, DateTime createdAt) : base(id)
    {
        ExecutionId = executionId;
        ProviderId = providerId;
        ResultType = Required(resultType);
        SchemaVersion = Required(schemaVersion);
        DataJson = Required(dataJson);
        MarkAsCreated(createdAt, null);
    }

    public Guid ExecutionId { get; private set; }
    public Guid ProviderId { get; private set; }
    public string ResultType { get; private set; } = string.Empty;
    public string SchemaVersion { get; private set; } = string.Empty;
    public string DataJson { get; private set; } = "{}";

    public static AgentResult Create(Guid executionId, Guid providerId, string resultType, string schemaVersion,
        string dataJson, DateTime? createdAt = null)
        => new(Guid.NewGuid(), executionId, providerId, resultType, schemaVersion, dataJson, createdAt ?? DateTime.UtcNow);

    private static string Required(string value)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
}
