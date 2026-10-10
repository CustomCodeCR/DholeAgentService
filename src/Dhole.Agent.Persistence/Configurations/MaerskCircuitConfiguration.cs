using Dhole.Agent.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class MaerskCircuitConfiguration : IEntityTypeConfiguration<MaerskCircuitRecord>
{
    public void Configure(EntityTypeBuilder<MaerskCircuitRecord> entity)
    {
        entity.ToTable("maersk_circuits");
        entity.HasKey(x => x.ProviderId);
        entity.Property(x => x.ProviderId).HasColumnName("provider_id");
        entity.Property(x => x.State).HasColumnName("state").HasMaxLength(20).IsRequired();
        entity.Property(x => x.RequiresOperator).HasColumnName("requires_operator");
        entity.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(120);
        entity.Property(x => x.OpenUntilUtc).HasColumnName("open_until_utc");
        entity.Property(x => x.ConsecutiveFailures).HasColumnName("consecutive_failures");
        entity.Property(x => x.ProbeExecutionId).HasColumnName("probe_execution_id");
        entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
    }
}
