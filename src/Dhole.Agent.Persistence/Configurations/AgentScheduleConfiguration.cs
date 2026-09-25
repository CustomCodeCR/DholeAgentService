using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentScheduleConfiguration : EntityTypeConfigurationBase<AgentSchedule, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentSchedule> builder)
    {
        base.Configure(builder);
        builder.ToTable("AgentSchedules");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(250).IsRequired();
        builder.Property(x => x.ScheduleType).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.CronExpression).HasMaxLength(150);
        builder.Property(x => x.Timezone).HasMaxLength(100).IsRequired();
        builder.Property(x => x.InputJson).HasColumnType("jsonb").IsRequired();
        builder.HasOne<AgentDefinition>().WithMany().HasForeignKey(x => x.AgentDefinitionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AgentProvider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AgentCredential>().WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AgentExtractionProfile>().WithMany().HasForeignKey(x => x.ExtractionProfileId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.ExtractionProfileId);
        builder.HasIndex(x => x.NextExecutionAt);
        builder.HasIndex(x => x.IsActive);
        builder.Property(x => x.IsActive).IsRequired();
    }
}
