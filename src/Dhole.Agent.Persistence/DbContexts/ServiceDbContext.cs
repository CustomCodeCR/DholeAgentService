using System.Text.Json;
using CustomCodeFramework.Core.Domain.Entities;
using CustomCodeFramework.Messaging.Inbox;
using CustomCodeFramework.Messaging.Outbox;
using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using CustomCodeFramework.Postgres.EntityFramework.DbContexts;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dhole.Agent.Persistence.DbContexts;

public sealed class ServiceDbContext(DbContextOptions<ServiceDbContext> options,
    IConfiguration? realtimeConfiguration = null) : AppDbContextBase(options)
{
    private const string SourceService = "DholeAgentService";

    public DbSet<AgentProvider> AgentProviders => Set<AgentProvider>();
    public DbSet<AgentDefinition> AgentDefinitions => Set<AgentDefinition>();
    public DbSet<AgentCredential> AgentCredentials => Set<AgentCredential>();
    public DbSet<AgentExtractionProfile> AgentExtractionProfiles => Set<AgentExtractionProfile>();
    public DbSet<AgentExtractionRoute> AgentExtractionRoutes => Set<AgentExtractionRoute>();
    public DbSet<AgentExtractionEquipment> AgentExtractionEquipment => Set<AgentExtractionEquipment>();
    public DbSet<AgentEndpointCapture> AgentEndpointCaptures => Set<AgentEndpointCapture>();
    public DbSet<AgentExtractionField> AgentExtractionFields => Set<AgentExtractionField>();
    public DbSet<BrowserProfile> BrowserProfiles => Set<BrowserProfile>();
    public DbSet<AgentSchedule> AgentSchedules => Set<AgentSchedule>();
    public DbSet<AgentExecution> AgentExecutions => Set<AgentExecution>();
    public DbSet<AgentExecutionLog> AgentExecutionLogs => Set<AgentExecutionLog>();
    public DbSet<AgentResult> AgentResults => Set<AgentResult>();
    public DbSet<Dhole.Agent.Persistence.Repositories.MaerskHealthAlertRecord> MaerskHealthAlerts => Set<Dhole.Agent.Persistence.Repositories.MaerskHealthAlertRecord>();
    public DbSet<Dhole.Agent.Persistence.Repositories.MaerskCircuitEventRecord> MaerskCircuitEvents => Set<Dhole.Agent.Persistence.Repositories.MaerskCircuitEventRecord>();
    public DbSet<Dhole.Agent.Persistence.Repositories.MaerskCircuitRecord> MaerskCircuits => Set<Dhole.Agent.Persistence.Repositories.MaerskCircuitRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        AppendRealtimeInvalidations();
        AddDomainEventsToOutbox();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        AppendRealtimeInvalidations();
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

    private void AppendRealtimeInvalidations()
    {
        if (!AgentRealtimeInvalidation.IsEnabled(realtimeConfiguration))
            return;
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var resource = entry.Entity switch
            {
                AgentExecution item => ("execution", item.Id),
                AgentDefinition item => ("definition", item.Id),
                AgentSchedule item => ("schedule", item.Id),
                _ => ("", Guid.Empty)
            };
            if (resource.Item2 == Guid.Empty) continue;
            var eventId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var correlation = eventId.ToString("D");
            var eventType = AgentRealtimeInvalidation.EventType(resource.Item1);
            OutboxMessages.Add(new OutboxMessage
            {
                EventId = eventId,
                EventType = eventType,
                EventName = eventType,
                SourceService = SourceService,
                PayloadJson = AgentRealtimeInvalidation.Serialize(
                    eventId, resource.Item2, resource.Item1,
                    AgentRealtimeInvalidation.NextVersionHint(), now, correlation),
                CorrelationId = correlation,
                Status = OutboxMessageStatus.Pending,
                RetryCount = 0,
                CreatedAtUtc = now.UtcDateTime
            });
        }
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
