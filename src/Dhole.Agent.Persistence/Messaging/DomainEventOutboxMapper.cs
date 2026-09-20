using CustomCodeFramework.Core.Domain.Events;
using Dhole.Agent.Domain.Agents.Events;

namespace Dhole.Agent.Persistence.Messaging;

internal static class DomainEventOutboxMapper
{
    public static string GetEventName(IDomainEvent domainEvent) => domainEvent switch
    {
        AgentProviderCreatedDomainEvent => "agent.provider.created",
        AgentProviderUpdatedDomainEvent => "agent.provider.updated",
        AgentProviderActivatedDomainEvent => "agent.provider.activated",
        AgentProviderInactivatedDomainEvent => "agent.provider.inactivated",
        AgentScheduleCreatedDomainEvent => "agent.schedule.created",
        AgentScheduleUpdatedDomainEvent => "agent.schedule.updated",
        AgentScheduleActivatedDomainEvent => "agent.schedule.activated",
        AgentScheduleInactivatedDomainEvent => "agent.schedule.inactivated",
        AgentExecutionRequestedDomainEvent => "agent.execution.requested",
        AgentExecutionStartedDomainEvent => "agent.execution.started",
        AgentExecutionCompletedDomainEvent => "agent.execution.completed",
        AgentExecutionFailedDomainEvent => "agent.execution.failed",
        BrowserProfileAuthenticatedDomainEvent => "agent.browser-profile.authenticated",
        BrowserProfileExpiredDomainEvent => "agent.browser-session.expired",
        OceanFreightRatesExtractedDomainEvent => "agent.ocean-freight-rates.extracted",
        _ => $"agent.{domainEvent.GetType().Name}"
    };

    public static string GetEventType(IDomainEvent domainEvent)
        => domainEvent.GetType().FullName ?? domainEvent.GetType().Name;
}
