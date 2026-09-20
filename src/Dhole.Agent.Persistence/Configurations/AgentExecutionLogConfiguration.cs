using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentExecutionLogConfiguration : EntityTypeConfigurationBase<AgentExecutionLog, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentExecutionLog> builder)
    {
        base.Configure(builder);
        builder.ToTable("AgentExecutionLogs");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Level).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Category).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnType("jsonb");
        builder.HasOne<AgentExecution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.ExecutionId, x.OccurredAt });
    }
}
