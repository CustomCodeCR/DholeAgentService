using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentExecutionConfiguration : EntityTypeConfigurationBase<AgentExecution, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentExecution> builder)
    {
        base.Configure(builder);
        builder.ToTable("AgentExecutions");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ExecutionType).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(60).IsRequired();
        builder.Property(x => x.InputJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.OutputJson).HasColumnType("jsonb");
        builder.Property(x => x.ErrorCode).HasMaxLength(120);
        builder.Property(x => x.ErrorMessage).HasMaxLength(4000);
        builder.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.TraceId).HasMaxLength(150);
        builder.HasOne<AgentDefinition>().WithMany().HasForeignKey(x => x.AgentDefinitionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AgentProvider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AgentSchedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AgentCredential>().WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.ProviderId);
        builder.HasIndex(x => x.CreatedAtUtc);
        builder.HasIndex(x => x.CorrelationId);
    }
}
