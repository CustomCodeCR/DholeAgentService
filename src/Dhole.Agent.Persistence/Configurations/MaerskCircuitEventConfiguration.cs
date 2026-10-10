using Dhole.Agent.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class MaerskCircuitEventConfiguration : IEntityTypeConfiguration<MaerskCircuitEventRecord>
{
    public void Configure(EntityTypeBuilder<MaerskCircuitEventRecord> entity)
    {
        entity.ToTable("maersk_circuit_events");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).HasColumnName("id");
        entity.Property(x => x.ProviderId).HasColumnName("provider_id");
        entity.Property(x => x.ExecutionId).HasColumnName("execution_id");
        entity.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(40).IsRequired();
        entity.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(120);
        entity.Property(x => x.ActorId).HasColumnName("actor_id");
        entity.Property(x => x.OperatorReason).HasColumnName("operator_reason").HasMaxLength(1000);
        entity.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
        entity.HasIndex(x => new { x.ProviderId, x.OccurredAtUtc });
        entity.HasIndex(x => new { x.ProviderId, x.ExecutionId, x.EventType, x.ReasonCode })
            .IsUnique().HasFilter("execution_id IS NOT NULL");
    }
}
