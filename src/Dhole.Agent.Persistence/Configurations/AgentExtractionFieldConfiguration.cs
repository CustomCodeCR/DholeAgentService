using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentExtractionFieldConfiguration : EntityTypeConfigurationBase<AgentExtractionField, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentExtractionField> builder)
    {
        base.Configure(builder);
        builder.ToTable("extraction_fields");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Key).HasMaxLength(150).IsRequired();
        builder.Property(x => x.Label).HasMaxLength(250).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.DataType).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.JsonPath).HasMaxLength(2000);
        builder.Property(x => x.Required).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.HasOne<AgentExtractionProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.ProfileId, x.Key }).IsUnique();
        builder.HasIndex(x => new { x.ProfileId, x.SortOrder });
    }
}
