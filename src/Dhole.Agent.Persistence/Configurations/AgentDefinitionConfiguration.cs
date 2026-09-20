using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentDefinitionConfiguration : EntityTypeConfigurationBase<AgentDefinition, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentDefinition> builder)
    {
        base.Configure(builder);
        builder.ToTable("AgentDefinitions");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Code).HasMaxLength(120).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.ActionType).HasConversion<string>().HasMaxLength(80).IsRequired();
        builder.Property(x => x.ExecutionStrategy).HasConversion<string>().HasMaxLength(80).IsRequired();
        builder.Property(x => x.ConfigurationJson).HasColumnType("jsonb");
        builder.HasOne<AgentProvider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ProviderId);
        builder.Property(x => x.IsActive).IsRequired();
    }
}
