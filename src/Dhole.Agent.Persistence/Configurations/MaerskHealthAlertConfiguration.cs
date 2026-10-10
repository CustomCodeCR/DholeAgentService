using Dhole.Agent.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class MaerskHealthAlertConfiguration : IEntityTypeConfiguration<MaerskHealthAlertRecord>
{
    public void Configure(EntityTypeBuilder<MaerskHealthAlertRecord> e)
    {
        e.ToTable("maersk_health_alerts");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id");
        e.Property(x => x.ProviderId).HasColumnName("provider_id");
        e.Property(x => x.AlertKey).HasColumnName("alert_key").HasMaxLength(80);
        e.Property(x => x.Code).HasColumnName("code").HasMaxLength(120);
        e.Property(x => x.Severity).HasColumnName("severity").HasMaxLength(16);
        e.Property(x => x.State).HasColumnName("state").HasMaxLength(16);
        e.Property(x => x.Occurrences).HasColumnName("occurrences");
        e.Property(x => x.FirstSeenAtUtc).HasColumnName("first_seen_at_utc");
        e.Property(x => x.LastSeenAtUtc).HasColumnName("last_seen_at_utc");
        e.Property(x => x.ResolvedAtUtc).HasColumnName("resolved_at_utc");
        e.Property(x => x.AcknowledgedAtUtc).HasColumnName("acknowledged_at_utc");
        e.Property(x => x.AcknowledgedBy).HasColumnName("acknowledged_by");
        e.HasIndex(x => new { x.ProviderId, x.AlertKey }).IsUnique();
        e.HasIndex(x => new { x.ProviderId, x.State });
    }
}
