using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentExtractionProfileConfiguration : EntityTypeConfigurationBase<AgentExtractionProfile, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentExtractionProfile> builder)
    {
        base.Configure(builder);
        builder.ToTable("extraction_profiles");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.BaseUrl).HasMaxLength(1000);
        builder.Property(x => x.LoginUrl).HasMaxLength(1000);
        builder.Property(x => x.SearchUrl).HasMaxLength(1000);
        builder.Property(x => x.PromptTemplate).HasColumnType("text").IsRequired();
        builder.Property(x => x.ExecutionStrategy).HasConversion<string>().HasMaxLength(80).IsRequired();
        builder.Property(x => x.ParserKey).HasMaxLength(150);
        builder.Property(x => x.IsActive).IsRequired();
        builder.HasOne<AgentProvider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AgentCredential>().WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.ProviderId);
        builder.HasIndex(x => x.IsActive);
    }
}
