using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentEndpointCaptureConfiguration : EntityTypeConfigurationBase<AgentEndpointCapture, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentEndpointCapture> builder)
    {
        base.Configure(builder);
        builder.ToTable("endpoint_captures");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.HttpMethod).HasMaxLength(20).IsRequired();
        builder.Property(x => x.UrlPattern).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.MatchType).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(200);
        builder.Property(x => x.CaptureRequest).IsRequired();
        builder.Property(x => x.CaptureResponse).IsRequired();
        builder.Property(x => x.IsRequired).IsRequired();
        builder.Property(x => x.TimeoutSeconds).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
        builder.HasOne<AgentExtractionProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.ProfileId, x.SortOrder });
    }
}
