using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentResultConfiguration : EntityTypeConfigurationBase<AgentResult, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentResult> builder)
    {
        base.Configure(builder);
        builder.ToTable("AgentResults");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ResultType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.SchemaVersion).HasMaxLength(30).IsRequired();
        builder.Property(x => x.DataJson).HasColumnType("jsonb").IsRequired();
        builder.HasOne<AgentExecution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AgentProvider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ExecutionId).IsUnique();
        builder.HasIndex(x => x.ProviderId);
    }
}
