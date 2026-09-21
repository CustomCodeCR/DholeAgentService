using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentExecutionTaskConfiguration : EntityTypeConfigurationBase<AgentExecutionTask, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentExecutionTask> builder)
    {
        base.Configure(builder);
        builder.ToTable("execution_tasks");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.InputJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.ErrorCode).HasMaxLength(120);
        builder.Property(x => x.ErrorMessage).HasMaxLength(4000);
        builder.Property(x => x.SortOrder).IsRequired();
        builder.HasOne<AgentExecution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AgentExtractionRoute>().WithMany().HasForeignKey(x => x.RouteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AgentExtractionEquipment>().WithMany().HasForeignKey(x => x.EquipmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ExecutionId, x.SortOrder });
        builder.HasIndex(x => x.Status);
    }
}
