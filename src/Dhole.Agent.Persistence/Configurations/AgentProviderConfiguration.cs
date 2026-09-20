using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentProviderConfiguration : EntityTypeConfigurationBase<AgentProvider, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentProvider> builder)
    {
        base.Configure(builder);
        builder.ToTable("AgentProviders");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Code).HasMaxLength(80).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ProviderType).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.BaseUrl).HasMaxLength(1000);
        builder.Property(x => x.DefaultExecutionStrategy).HasConversion<string>().HasMaxLength(80).IsRequired();
        builder.Property(x => x.MetadataJson).HasColumnType("jsonb");
        builder.Property(x => x.IsSystem).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
    }
}
