using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentExtractionRouteConfiguration : EntityTypeConfigurationBase<AgentExtractionRoute, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentExtractionRoute> builder)
    {
        base.Configure(builder);
        builder.ToTable("extraction_routes");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.PolCode).HasMaxLength(100);
        builder.Property(x => x.PolName).HasMaxLength(300).IsRequired();
        builder.Property(x => x.PoeCode).HasMaxLength(100);
        builder.Property(x => x.PoeName).HasMaxLength(300);
        builder.Property(x => x.PodCode).HasMaxLength(100);
        builder.Property(x => x.PodName).HasMaxLength(300).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
        builder.HasOne<AgentExtractionProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.ProfileId, x.SortOrder });
    }
}
