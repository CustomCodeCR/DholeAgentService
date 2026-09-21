using System.Text.Json;
using CustomCodeFramework.Core.Domain.Entities;
using CustomCodeFramework.Messaging.Inbox;
using CustomCodeFramework.Messaging.Outbox;
using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using CustomCodeFramework.Postgres.EntityFramework.DbContexts;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.Persistence.DbContexts;

public sealed class ServiceDbContext(DbContextOptions<ServiceDbContext> options) : AppDbContextBase(options)
{
    private const string SourceService = "DholeAgentService";

    public DbSet<AgentProvider> AgentProviders => Set<AgentProvider>();
    public DbSet<AgentDefinition> AgentDefinitions => Set<AgentDefinition>();
    public DbSet<AgentCredential> AgentCredentials => Set<AgentCredential>();
    public DbSet<AgentExtractionProfile> AgentExtractionProfiles => Set<AgentExtractionProfile>();
    public DbSet<AgentExtractionRoute> AgentExtractionRoutes => Set<AgentExtractionRoute>();
    public DbSet<AgentExtractionEquipment> AgentExtractionEquipment => Set<AgentExtractionEquipment>();
    public DbSet<AgentEndpointCapture> AgentEndpointCaptures => Set<AgentEndpointCapture>();
    public DbSet<BrowserProfile> BrowserProfiles => Set<BrowserProfile>();
    public DbSet<AgentSchedule> AgentSchedules => Set<AgentSchedule>();
    public DbSet<AgentExecution> AgentExecutions => Set<AgentExecution>();
    public DbSet<AgentExecutionLog> AgentExecutionLogs => Set<AgentExecutionLog>();
    public DbSet<AgentResult> AgentResults => Set<AgentResult>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        AddDomainEventsToOutbox();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        AddDomainEventsToOutbox();
        return base.SaveChanges();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("agent");
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ServiceDbContext).Assembly);
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
    }

    private void AddDomainEventsToOutbox()
    {
        var roots = ChangeTracker.Entries()
            .Select(x => x.Entity)
            .OfType<AggregateRoot<Guid>>()
            .Where(x => x.DomainEvents.Count > 0)
            .ToList();

        if (roots.Count == 0) return;

        var messages = new List<OutboxMessage>();
        foreach (var root in roots)
        {
            foreach (var domainEvent in root.DomainEvents)
            {
                messages.Add(new OutboxMessage
                {
                    EventId = domainEvent.EventId,
                    EventType = DomainEventOutboxMapper.GetEventType(domainEvent),
                    EventName = DomainEventOutboxMapper.GetEventName(domainEvent),
                    SourceService = SourceService,
                    PayloadJson = JsonSerializer.Serialize(domainEvent, domainEvent.GetType()),
                    HeadersJson = null,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Status = OutboxMessageStatus.Pending,
                    RetryCount = 0,
                    ErrorMessage = null,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
            root.ClearDomainEvents();
        }

        OutboxMessages.AddRange(messages);
    }
}
