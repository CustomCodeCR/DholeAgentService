using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentExtractionEquipmentConfiguration : EntityTypeConfigurationBase<AgentExtractionEquipment, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentExtractionEquipment> builder)
    {
        base.Configure(builder);
        builder.ToTable("extraction_equipment");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Code).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Quantity).IsRequired();
        builder.Property(x => x.DefaultWeightKg).HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
        builder.HasOne<AgentExtractionProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.ProfileId, x.Code });
        builder.HasIndex(x => new { x.ProfileId, x.SortOrder });
    }
}
