using CustomCodeFramework.Messaging.Events;

namespace Dhole.Agent.Contracts.Events;

public sealed record ServiceCreatedIntegrationEvent : IntegrationEvent
{
    public required Guid EntityId { get; init; }
}
